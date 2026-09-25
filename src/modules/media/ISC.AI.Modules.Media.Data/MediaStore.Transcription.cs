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
/// Запись расшифровки речи (ADR-0026): статус носителя и фрагменты. Работает БЕЗ контекста доступа — от имени
/// системы, как и индексация лиц: фоновому конвейеру субъект не нужен, а гриф и подразделение фрагменты
/// берут у самого носителя (ТБ-020/070) — вызывающий их не передаёт и подменить не может.
/// </summary>
public sealed partial class MediaStore
{
    /// <summary>Предел длины причины неудачи (столбец <c>transcript_error</c>).</summary>
    private const int TranscriptErrorMaxLength = 2000;

    /// <inheritdoc />
    public async Task<MediaAssetTranscriptionInfo?> GetForTranscriptionAsync(int assetId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Assets.AsNoTracking()
            .Where(a => a.Id == assetId)
            .Select(a => new MediaAssetTranscriptionInfo(a.Id, a.Kind, a.StoredFileName, a.Classification, a.DivisionId))
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Проверка и перевод — ОДИН условный UPDATE, а не «прочитать статус, потом записать»: два параллельных
    /// запроса не пройдут оба (второй увидит уже «в очереди» и получит 0 строк). Причина прежней неудачи
    /// снимается; прежние фрагменты остаются до успешного прогона.
    /// </remarks>
    public async Task<bool> TryMarkTranscriptionPendingAsync(int assetId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var updated = await db.Assets
            .Where(a => a.Id == assetId
                && a.TranscriptStatus != TranscriptStatus.Pending
                && a.TranscriptStatus != TranscriptStatus.Processing)
            .ExecuteUpdateAsync(s => s
                .SetProperty(a => a.TranscriptStatus, TranscriptStatus.Pending)
                .SetProperty(a => a.TranscriptError, (string?)null), cancellationToken);
        return updated > 0;
    }

    /// <inheritdoc />
    public Task MarkTranscriptionProcessingAsync(int assetId, CancellationToken cancellationToken = default) =>
        SetTranscriptStatusAsync(assetId, TranscriptStatus.Processing, error: null, cancellationToken);

    /// <inheritdoc />
    public Task FailTranscriptionAsync(int assetId, string reason, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reason);
        var message = reason.Length > TranscriptErrorMaxLength ? reason[..TranscriptErrorMaxLength] : reason;
        return SetTranscriptStatusAsync(assetId, TranscriptStatus.Failed, message, cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Прежние фрагменты НЕ снимаются ни при постановке в очередь, ни при неудаче: неудачная повторная
    /// расшифровка не должна уничтожать уже имеющуюся — фрагменты заменяются только успешным прогоном, целиком
    /// и одной транзакцией (половинчатой расшифровки нет). Строка носителя блокируется на время транзакции
    /// (<c>FOR UPDATE</c>): два прогона одного носителя (повтор, поставленный во время идущего) выполняются по
    /// очереди, а не перемешивают фрагменты и не упираются в уникальный индекс (носитель, номер). Ту же
    /// блокировку первым делом берёт уничтожение носителя (<c>MediaPurger</c>, ТБ-064): число фрагментов в акте
    /// совпадает с тем, что снесёт каскад; носитель уничтожен раньше — строки нет, и запись отказывает.
    /// </remarks>
    public async Task CompleteTranscriptionAsync(
        int assetId,
        IReadOnlyList<TranscriptSegmentDraft> segments,
        string modelVersion,
        long? durationMs = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(segments);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelVersion);
        ValidateSegments(segments);

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // Блокировка строки носителя до конца транзакции (см. remarks). Идентификатор — параметром.
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT 1 FROM media.asset WHERE id = {assetId} FOR UPDATE", cancellationToken);

        var asset = await db.Assets.SingleOrDefaultAsync(a => a.Id == assetId, cancellationToken)
            ?? throw new InvalidOperationException(
                "Носитель " + assetId.ToString(CultureInfo.InvariantCulture) + " не найден: расшифровку записать некуда.");

        await db.TranscriptSegments.Where(s => s.AssetId == assetId).ExecuteDeleteAsync(cancellationToken);

        foreach (var segment in segments)
        {
            db.TranscriptSegments.Add(new TranscriptSegment
            {
                AssetId = assetId,
                Index = segment.Index,
                StartMs = segment.StartMs,
                EndMs = segment.EndMs,

                // ДОСЛОВНО (ADR-0026): первичный слой не нормализуется и не обрезается.
                Text = segment.Text,
                ModelVersion = modelVersion,

                // Денормализация режимных полей С НОСИТЕЛЯ (ТБ-020): строка самодостаточна для решётки.
                Classification = asset.Classification,
                DivisionId = asset.DivisionId,
            });
        }

        asset.TranscriptStatus = TranscriptStatus.Done;
        asset.TranscriptError = null;
        asset.TranscriberVersion = modelVersion;
        asset.TranscribedAt = DateTime.UtcNow;

        // Длительность ЗАПИСИ от распознавателя (длина подготовленного звука, не конец последней речи). У аудио
        // другого измерителя нет — пишется всегда, когда известна, поверх прежнего значения: повторная
        // расшифровка исправляет и старую оценку «до конца речи». У видео длительность даёт раскадровка, и
        // значение распознавателя подставляется, только если её ещё нет (раскадровка не удалась или не шла).
        if (asset.Kind == MediaKind.Audio)
        {
            if (durationMs is >= 0)
            {
                asset.DurationMs = durationMs;
            }
        }
        else if (asset.DurationMs is null && durationMs is > 0)
        {
            asset.DurationMs = durationMs;
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// Отсекает заведомо испорченный выход распознавателя ДО записи: без текста, с отрицательным или
    /// перевёрнутым таймкодом, с повтором номера. Такой фрагмент не может быть показан как «место записи»,
    /// и лучше честная неудача расшифровки, чем фрагмент, ведущий не туда.
    /// </summary>
    private static void ValidateSegments(IReadOnlyList<TranscriptSegmentDraft> segments)
    {
        var indexes = new HashSet<int>();
        foreach (var segment in segments)
        {
            if (segment is null || segment.Text is null)
            {
                throw new ArgumentException("Фрагмент расшифровки без текста.", nameof(segments));
            }

            if (segment.Index < 0 || segment.StartMs < 0 || segment.EndMs < segment.StartMs)
            {
                throw new ArgumentException(
                    $"Фрагмент расшифровки №{segment.Index}: неверные номер или таймкоды ({segment.StartMs}–{segment.EndMs} мс).",
                    nameof(segments));
            }

            if (!indexes.Add(segment.Index))
            {
                throw new ArgumentException($"Номер фрагмента расшифровки {segment.Index} повторяется.", nameof(segments));
            }
        }
    }

    private async Task SetTranscriptStatusAsync(
        int assetId, TranscriptStatus status, string? error, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await db.Assets.Where(a => a.Id == assetId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(a => a.TranscriptStatus, status)
                .SetProperty(a => a.TranscriptError, error), cancellationToken);
    }
}
