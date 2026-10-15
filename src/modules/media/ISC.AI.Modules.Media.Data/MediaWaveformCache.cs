using ISC.AI.Modules.Media.Domain.Model;
using ISC.AI.Modules.Media.Domain.Services;
using Microsoft.EntityFrameworkCore;

namespace ISC.AI.Modules.Media.Data;

/// <summary>
/// Кэш волны аудиозаписи в строке носителя (<c>media.asset.waveform_*</c>, ADR-0047). Режимных полей у волны нет: это
/// производная носителя, и читает её только эндпоинт волны ПОСЛЕ проверки допуска к самому носителю (floor ядра и
/// область дел субъекта) — как ленту кадров видео (ADR-0038).
/// </summary>
public sealed class MediaWaveformCache(IDbContextFactory<MediaDbContext> contextFactory) : IAudioWaveformCache
{
    /// <inheritdoc />
    public async Task<AudioWaveform?> GetAsync(int assetId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var row = await db.Assets
            .Where(a => a.Id == assetId && a.WaveformVersion == AudioWaveform.CurrentVersion
                && a.WaveformPeaks != null && a.WaveformStepMs != null)
            .Select(a => new { a.WaveformPeaks, a.WaveformStepMs })
            .FirstOrDefaultAsync(cancellationToken);
        return row is null ? null : new AudioWaveform(row.WaveformStepMs!.Value, row.WaveformPeaks!, AudioWaveform.CurrentVersion);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Одним UPDATE без чтения строки: две вкладки, открывшие карточку одновременно, построят одну и ту же волну
    /// (оригинал не меняется, ТЭ-007), и последняя запись просто совпадёт с первой.
    /// </remarks>
    public async Task SaveAsync(int assetId, AudioWaveform waveform, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(waveform);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await db.Assets
            .Where(a => a.Id == assetId)
            .ExecuteUpdateAsync(
                s => s
                    .SetProperty(a => a.WaveformPeaks, waveform.Peaks)
                    .SetProperty(a => a.WaveformStepMs, (int?)waveform.StepMs)
                    .SetProperty(a => a.WaveformVersion, (short?)waveform.Version),
                cancellationToken);
    }
}
