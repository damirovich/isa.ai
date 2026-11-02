using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ISC.AI.Modules.Media.Data.Entities;
using ISC.AI.Modules.Media.Domain.Model;
using Microsoft.EntityFrameworkCore;

namespace ISC.AI.Modules.Media.Data;

/// <summary>
/// Части незавершённой индексации видео (ТНД-007, ADR-0064): запись по ходу раскадровки, чтение для повтора, снятие.
/// Опубликованный результат они не трогают — публикация по-прежнему одна транзакция <see cref="CompleteIndexingAsync"/>.
/// </summary>
public sealed partial class MediaStore
{
    /// <inheritdoc />
    public async Task<IndexCheckpoint?> LoadIndexCheckpointAsync(
        int assetId, string runKey, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runKey);

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var parts = await db.IndexCheckpoints.AsNoTracking()
            .Where(c => c.AssetId == assetId)
            .OrderBy(c => c.FirstFrameIndex)
            .Select(c => new { c.RunKey, c.LastFrameIndex, c.Faces })
            .ToListAsync(cancellationToken);

        // Хотя бы одна часть чужого прогона — повтор с этими частями невозможен: лица разных моделей и порогов в одном
        // видео не смешиваются. Снимает такие части индексатор, начиная прогон заново.
        if (parts.Count == 0 || parts.Exists(p => !string.Equals(p.RunKey, runKey, StringComparison.Ordinal)))
        {
            return null;
        }

        var faces = new List<IndexedFace>();
        foreach (var part in parts)
        {
            faces.AddRange(IndexCheckpointFormat.Read(part.Faces));
        }

        return new IndexCheckpoint(parts[^1].LastFrameIndex, faces);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Блокировка строки носителя (<c>FOR UPDATE</c>) — та же, что у публикации результата, уничтожения носителя
    /// (<see cref="MediaPurger"/>) и регламента закрытого дела: акт уничтожения не может разойтись с тем, что снесёт каскад, а
    /// часть не пишется к носителю, которого уже нет. Проверка перекрытия — под той же блокировкой; уникальный индекс
    /// <c>(asset_id, first_frame_index)</c> — страховка.
    /// </remarks>
    public async Task SaveIndexCheckpointAsync(
        int assetId,
        string runKey,
        int firstFrameIndex,
        int lastFrameIndex,
        IReadOnlyList<IndexedFace> faces,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runKey);
        ArgumentNullException.ThrowIfNull(faces);
        ArgumentOutOfRangeException.ThrowIfLessThan(lastFrameIndex, firstFrameIndex);
        if (faces.Any(f => f.FrameIndex is not { } index || index < firstFrameIndex || index > lastFrameIndex))
        {
            throw new ArgumentException("Лицо части индексации вне её диапазона кадров.", nameof(faces));
        }

        var json = IndexCheckpointFormat.Write(faces);

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT 1 FROM media.asset WHERE id = {assetId} FOR UPDATE", cancellationToken);
        if (!await db.Assets.AnyAsync(a => a.Id == assetId, cancellationToken))
        {
            throw new InvalidOperationException(
                "Носитель " + assetId.ToString(CultureInfo.InvariantCulture) + " не найден: часть индексации записать некуда.");
        }

        // Части дописываются строго по возрастанию кадров одним прогоном. Чужой ключ или кадр, уже лежащий в части, —
        // значит, параллельно идёт другой прогон того же носителя: запись дала бы дубли лиц (ТНД-002).
        var conflict = await db.IndexCheckpoints.AnyAsync(
            c => c.AssetId == assetId && (c.RunKey != runKey || c.LastFrameIndex >= firstFrameIndex),
            cancellationToken);
        if (conflict)
        {
            throw new InvalidOperationException(
                "Часть индексации носителя " + assetId.ToString(CultureInfo.InvariantCulture)
                + " не записана: кадры уже сохранены или сохранены другим прогоном.");
        }

        db.IndexCheckpoints.Add(new MediaIndexCheckpoint
        {
            AssetId = assetId,
            RunKey = runKey,
            FirstFrameIndex = firstFrameIndex,
            LastFrameIndex = lastFrameIndex,
            FaceCount = faces.Count,
            Faces = json,
        });
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<int> DiscardIndexCheckpointAsync(int assetId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.IndexCheckpoints.Where(c => c.AssetId == assetId).ExecuteDeleteAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<int>> ListIndexCheckpointAssetsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.IndexCheckpoints.AsNoTracking()
            .Select(c => c.AssetId)
            .Distinct()
            .OrderBy(id => id)
            .ToListAsync(cancellationToken);
    }
}
