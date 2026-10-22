using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ISC.AI.Modules.Media.Data.EntityConfigurations;
using ISC.AI.Modules.Media.Domain.Model;
using Microsoft.EntityFrameworkCore;

namespace ISC.AI.Modules.Media.Data;

/// <summary>
/// Правки дикторов расшифровки (ADR-0062): слой поверх автоматической разметки — имя, объединение, перенос реплики,
/// с автором и временем. Дословный текст фрагментов НЕ меняется (ТЭ-007). Доступ проверяет сценарий до вызова.
/// </summary>
public sealed partial class MediaStore
{
    /// <inheritdoc />
    public async Task<TranscriptSpeakerEditResult> RenameTranscriptSpeakerAsync(
        int assetId, int number, string? name, int editedByUserId, CancellationToken cancellationToken = default)
    {
        var trimmed = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        if (trimmed is { Length: > TranscriptSpeakerConfiguration.NameMaxLength })
        {
            return TranscriptSpeakerEditResult.Invalid;
        }

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var changed = await db.TranscriptSpeakers
            .Where(s => s.AssetId == assetId && s.Number == number)
            .ExecuteUpdateAsync(s => s
                .SetProperty(e => e.Name, trimmed)
                .SetProperty(e => e.EditedByUserId, editedByUserId)
                .SetProperty(e => e.EditedAt, DateTime.UtcNow), cancellationToken);
        return changed > 0 ? TranscriptSpeakerEditResult.Ok : TranscriptSpeakerEditResult.NotFound;
    }

    /// <inheritdoc />
    public async Task<TranscriptSpeakerEditResult> MergeTranscriptSpeakersAsync(
        int assetId, int number, int into, int editedByUserId, CancellationToken cancellationToken = default)
    {
        if (number == into)
        {
            return TranscriptSpeakerEditResult.Invalid;
        }

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var speakers = await db.TranscriptSpeakers.Where(s => s.AssetId == assetId).ToListAsync(cancellationToken);
        var source = speakers.FirstOrDefault(s => s.Number == number);
        var target = speakers.FirstOrDefault(s => s.Number == into);
        if (source is null || target is null)
        {
            return TranscriptSpeakerEditResult.NotFound;
        }

        // Слияние по кругу запрещено: цель не должна (через цепочку) вести обратно в источник.
        var merges = speakers.ToDictionary(s => (int)s.Number, s => (int?)s.MergedInto);
        if (TranscriptSpeakers.Effective(into, null, merges) == number)
        {
            return TranscriptSpeakerEditResult.Invalid;
        }

        source.MergedInto = (short)into;
        source.EditedByUserId = editedByUserId;
        source.EditedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return TranscriptSpeakerEditResult.Ok;
    }

    /// <inheritdoc />
    public async Task<TranscriptSpeakerEditResult> MoveTranscriptSegmentAsync(
        int assetId, int segmentIndex, int? speaker, int editedByUserId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        if (speaker is { } target)
        {
            var row = await db.TranscriptSpeakers.AsNoTracking()
                .Where(s => s.AssetId == assetId && s.Number == target)
                .Select(s => new { s.MergedInto })
                .FirstOrDefaultAsync(cancellationToken);
            if (row is null)
            {
                return TranscriptSpeakerEditResult.NotFound;
            }

            // К слитому диктору не переносят — только к тому, в кого он слит (иначе перенос незаметно «уплывёт»).
            if (row.MergedInto is not null)
            {
                return TranscriptSpeakerEditResult.Invalid;
            }
        }

        var changed = await db.TranscriptSegments
            .Where(s => s.AssetId == assetId && s.Index == segmentIndex)
            .ExecuteUpdateAsync(s => s
                .SetProperty(e => e.SpeakerOverride, speaker is { } value ? (short?)value : null)
                .SetProperty(e => e.SpeakerEditedByUserId, editedByUserId)
                .SetProperty(e => e.SpeakerEditedAt, DateTime.UtcNow), cancellationToken);
        return changed > 0 ? TranscriptSpeakerEditResult.Ok : TranscriptSpeakerEditResult.NotFound;
    }
}
