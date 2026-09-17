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
/// Хранилище носителей (ТС-010, ТП-004/005): приём файла с дедупликацией по SHA-256 и атомарная
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

            var existingId = await db.Assets
                .Where(a => a.DivisionId == draft.DivisionId && a.ContentHash == hash)
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
                CapturedAt = draft.CapturedAt,
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
