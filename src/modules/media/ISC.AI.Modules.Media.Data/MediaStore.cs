using System.Globalization;
using System.Security.Cryptography;
using ISC.AI.Abstractions.Storage;
using ISC.AI.Modules.Media.Data.Entities;
using ISC.AI.Modules.Media.Domain.Model;
using ISC.AI.Modules.Media.Domain.Services;
using Microsoft.EntityFrameworkCore;
using Pgvector;

namespace ISC.AI.Modules.Media.Data;

/// <summary>
/// Хранилище носителей (ТС-010, ТП-004/005): приём файла с дедупликацией по (подразделение, гриф, SHA-256) и атомарная
/// запись результата индексации. Байты — в <see cref="IFileStorage"/> ядра, метаданные — в схеме
/// <c>media</c>. Гриф/подразделение носителя ДЕНОРМАЛИЗУЮТСЯ на каждое лицо и шаблон (ТБ-020).
/// </summary>
public sealed partial class MediaStore(
    IDbContextFactory<MediaDbContext> contextFactory,
    IFileStorage fileStorage) : IMediaStore
{
    /// <inheritdoc />
    public async Task<MediaAssetReceipt> ReceiveAsync(
        MediaAssetDraft draft, Stream content, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(content);

        // Хеш считается по ПОЛНОМУ потоку до записи: дубликат не должен занимать место на диске.
        // Поток буферизуется во временный файл, чтобы читать дважды (сеть/браузер не перематываются).
        var tempPath = Path.Combine(Path.GetTempPath(), "iscai-media-" + Guid.NewGuid().ToString("N"));
        try
        {
            string hash;
            long size;
            await using (var temp = File.Create(tempPath))
            {
                await content.CopyToAsync(temp, cancellationToken);
                size = temp.Length;
                temp.Position = 0;
                hash = Convert.ToHexString(await SHA256.HashDataAsync(temp, cancellationToken));
            }

            await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

            // Ключ дедупликации — (подразделение, ГРИФ, хеш): гриф носителя равен грифу дела (ТБ-070), поэтому тот же
            // файл, загруженный в дело с другим грифом, — отдельный носитель со своими производными. Иначе второй
            // загрузчик получал бы «принят», а носитель оставался бы под режимом первого дела: невидим для дела
            // с меньшим грифом либо биометрия секретного дела — под низким грифом. Уникальный индекс — страховка.
            var existingId = await db.Assets
                .Where(a => a.DivisionId == draft.DivisionId
                    && a.Classification == draft.Classification
                    && a.ContentHash == hash)
                .Select(a => (int?)a.Id)
                .FirstOrDefaultAsync(cancellationToken);
            if (existingId is { } duplicateId)
            {
                return new MediaAssetReceipt(duplicateId, Duplicate: true);
            }

            // Идентификатор носителя нужен как подкаталог хранилища — строка создаётся первой, файл
            // пишется вторым; при сбое записи файла строка снимается (компенсация), не наоборот.
            var asset = new MediaAsset
            {
                Kind = draft.Kind,
                OriginalFileName = Path.GetFileName(draft.OriginalFileName),
                StoredFileName = "pending",
                ContentType = draft.ContentType,
                ContentHash = hash,
                ByteSize = size,
                Source = draft.Source,
                // Npgsql пишет timestamptz только со смещением 0: значение из UI приходит в поясе сервера
                // (+06:00 в контуре) — нормализуем к UTC, не доверяя вызывающему (как AsUtc у сессий).
                CapturedAt = draft.CapturedAt?.ToUniversalTime(),
                Classification = draft.Classification,
                DivisionId = draft.DivisionId,
                UploadedByUserId = draft.UploadedByUserId,
                IndexStatus = MediaIndexStatus.Uploaded,
            };
            db.Assets.Add(asset);
            await db.SaveChangesAsync(cancellationToken);

            var subPath = asset.Id.ToString(CultureInfo.InvariantCulture);
            try
            {
                await using var source = File.OpenRead(tempPath);
                var extension = Path.GetExtension(draft.OriginalFileName);
                asset.StoredFileName = await fileStorage.SaveAsync(
                    source, extension, MediaFileCategories.Originals, subPath, cancellationToken);
                await db.SaveChangesAsync(cancellationToken);
            }
            catch
            {
                db.Assets.Remove(asset);
                await db.SaveChangesAsync(CancellationToken.None);
                throw;
            }

            return new MediaAssetReceipt(asset.Id, Duplicate: false);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    /// <inheritdoc />
    public async Task<MediaAssetIndexingInfo?> GetForIndexingAsync(int assetId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var asset = await db.Assets.AsNoTracking()
            .Where(a => a.Id == assetId)
            .Select(a => new { a.Id, a.Kind, a.StoredFileName, a.ContentType, a.Classification, a.DivisionId })
            .FirstOrDefaultAsync(cancellationToken);
        if (asset is null)
        {
            return null;
        }

        // Имена прежних вырезок — индексатор удалит их с диска перед перезаписью (иначе сироты).
        var crops = await db.Faces.AsNoTracking()
            .Where(f => f.AssetId == assetId && f.CropStoredFileName != null)
            .Select(f => f.CropStoredFileName!)
            .ToListAsync(cancellationToken);

        return new MediaAssetIndexingInfo(
            asset.Id, asset.Kind, asset.StoredFileName, asset.ContentType, asset.Classification, asset.DivisionId, crops);
    }

    /// <inheritdoc />
    public async Task MarkProcessingAsync(int assetId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await db.Assets.Where(a => a.Id == assetId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(a => a.IndexStatus, MediaIndexStatus.Processing)
                .SetProperty(a => a.IndexError, (string?)null), cancellationToken);
    }

    /// <inheritdoc />
    public async Task FailIndexingAsync(int assetId, string reason, CancellationToken cancellationToken = default)
    {
        var message = reason.Length > 2000 ? reason[..2000] : reason;
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await db.Assets.Where(a => a.Id == assetId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(a => a.IndexStatus, MediaIndexStatus.Failed)
                .SetProperty(a => a.IndexError, message), cancellationToken);
    }
}
