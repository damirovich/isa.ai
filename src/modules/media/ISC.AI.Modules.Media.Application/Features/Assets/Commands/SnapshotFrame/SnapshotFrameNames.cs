using System;
using System.Globalization;
using System.IO;
using ISC.AI.Modules.Media.Domain.Model;

namespace ISC.AI.Modules.Media.Application.Features.Assets.Commands.SnapshotFrame;

/// <summary>
/// Реквизиты снимка кадра (ADR-0028): таймкод, имя файла, текст происхождения и содержимое записи журнала — в одном
/// месте, чтобы имя файла, реквизит «Источник» и журнал называли один и тот же кадр одинаково.
/// </summary>
/// <remarks>
/// Числа — в инвариантном формате (<see cref="CultureInfo.InvariantCulture"/>): реквизиты попадают в БД и журнал и не
/// должны зависеть от культуры потока хоста. Номер кадра <c>N</c> — по НАТИВНОЙ частоте
/// (<see cref="VideoProbe.FrameIndexAt(long, double)"/>), не индекс раскадровки; без частоты номера нет — реквизиты
/// несут только таймкод.
/// </remarks>
public static class SnapshotFrameNames
{
    /// <summary>MIME-тип снимка: PNG без потерь (ТЭ-007).</summary>
    public const string ContentType = "image/png";

    /// <summary>Расширение файла снимка.</summary>
    public const string Extension = ".png";

    /// <summary>Имя источника по умолчанию, если у видео нет пригодного имени файла.</summary>
    private const string FallbackBaseName = "видео";

    /// <summary>Таймкод <c>hh:mm:ss.mmm</c> момента <paramref name="timestampMs"/> (часы не ограничены сутками).</summary>
    public static string Timecode(long timestampMs)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(timestampMs);
        var time = TimeSpan.FromMilliseconds(timestampMs);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{(long)time.TotalHours:00}:{time.Minutes:00}:{time.Seconds:00}.{time.Milliseconds:000}");
    }

    /// <summary>
    /// Имя файла снимка: <c>&lt;имя видео без расширения&gt;_кадр&lt;N&gt;_&lt;hh-mm-ss-mmm&gt;.png</c>; без номера
    /// кадра — <c>…_кадр_&lt;hh-mm-ss-mmm&gt;.png</c>. Имя источника укорачивается так, чтобы итог уложился в
    /// <see cref="MediaFileRules.MaxFileNameLength"/> (предел столбца и загрузки).
    /// </summary>
    /// <param name="sourceFileName">Исходное имя файла видео (как отображается).</param>
    /// <param name="frameIndex">Номер кадра по нативной частоте; <see langword="null"/> — неизвестен.</param>
    /// <param name="timestampMs">Номинальный момент кадра, мс.</param>
    public static string FileName(string? sourceFileName, long? frameIndex, long timestampMs)
    {
        var suffix = "_кадр" + FrameNumber(frameIndex) + "_"
            + Timecode(timestampMs).Replace(':', '-').Replace('.', '-') + Extension;

        var baseName = Path.GetFileNameWithoutExtension(sourceFileName ?? string.Empty);
        if (string.IsNullOrWhiteSpace(baseName))
        {
            baseName = FallbackBaseName;
        }

        var room = MediaFileRules.MaxFileNameLength - suffix.Length;
        if (baseName.Length > room)
        {
            baseName = baseName[..room];
        }

        return baseName + suffix;
    }

    /// <summary>Реквизит «Источник»: «снимок кадра № N (hh:mm:ss.mmm) из носителя № X» (без N — если частота неизвестна).</summary>
    public static string Source(int sourceAssetId, long? frameIndex, long timestampMs) =>
        "снимок кадра" + FrameLabel(frameIndex) + " (" + Timecode(timestampMs) + ") из носителя № "
        + sourceAssetId.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Чувствительная часть записи журнала (ТБ-030): какой кадр и откуда, и что единственное с ним сделано —
    /// автоповорот по метке контейнера (ТЭ-007: иной обработки нет).
    /// </summary>
    public static string AuditPayload(int sourceAssetId, long? frameIndex, long timestampMs) =>
        "кадр" + FrameLabel(frameIndex) + " (" + Timecode(timestampMs) + ") из носителя № "
        + sourceAssetId.ToString(CultureInfo.InvariantCulture)
        + "; PNG из оригинала, автоповорот по метке контейнера (ТЭ-007)";

    private static string FrameLabel(long? frameIndex) =>
        frameIndex is { } index ? " № " + index.ToString(CultureInfo.InvariantCulture) : string.Empty;

    private static string FrameNumber(long? frameIndex) =>
        frameIndex is { } index ? index.ToString(CultureInfo.InvariantCulture) : string.Empty;
}
