using System;

namespace ISC.AI.Modules.Media.Domain.Model;

/// <summary>Кадр видео, извлечённый раскадровкой: порядковый номер, таймкод и байты JPEG.</summary>
public sealed record VideoFrame(int Index, TimeSpan Timestamp, byte[] JpegBytes);

/// <summary>
/// Параметры выборки кадров (ТО-мат-06): частота — кадров в секунду (по умолчанию 1 к/с).
/// Смена сцены и ограничение длительности — параметры извлекателя.
/// </summary>
/// <param name="FramesPerSecond">Сколько кадров брать в секунду видео (0.1..30).</param>
/// <param name="MaxFrames">Верхний предел кадров на носитель; <see langword="null"/> — без предела.</param>
public sealed record FrameSamplingOptions(double FramesPerSecond = 1.0, int? MaxFrames = null);

/// <summary>
/// Проба видеопотока (ffprobe, ADR-0028): частота кадров, длительность и размер кадра ПОСЛЕ автоповорота
/// (телефонная съёмка с меткой поворота показывается «прямо» — так же её отдаёт вырезка кадра).
/// </summary>
/// <remarks>
/// Частота — НАТИВНАЯ частота кадров файла (25, 29,97, 30…), а не частота выборки раскадровки
/// (<see cref="FrameSamplingOptions.FramesPerSecond"/>): по ней считается номер кадра при покадровом просмотре
/// (<c>N = round(t · fps)</c>) и шаг «предыдущий/следующий кадр». Индексы кадров раскадровки
/// (<see cref="VideoFrame.Index"/>) — другое понятие, они на нативную частоту не переводятся.
/// У записи с переменной частотой (VFR) берётся средняя частота: шаг кадра тогда приблизителен.
/// </remarks>
/// <param name="FrameRate">Кадров в секунду (> 0).</param>
/// <param name="Duration">
/// Длительность записи; <see langword="null"/> — контейнер её не сообщает (незавершённая запись Matroska/WebM,
/// оборванный файл). Ноль длительностью НЕ считается: он означал бы «кадров нет» и запер бы весь просмотр.
/// </param>
/// <param name="Width">Ширина кадра после автоповорота, пиксели.</param>
/// <param name="Height">Высота кадра после автоповорота, пиксели.</param>
/// <param name="RecordedAt">
/// «Встроенное» время начала записи из метаданных файла (ТФ-МЕД-11, ADR-0038) — по правилу
/// <see cref="RecordTimeMetadata.TryParse"/>; <see langword="null"/> — в файле его нет или оно неправдоподобно.
/// Это сведения из файла, а не доказательство: их легко сбить часами камеры или подделать.
/// </param>
public sealed record VideoProbe(double FrameRate, TimeSpan? Duration, int Width, int Height, DateTimeOffset? RecordedAt = null)
{
    /// <summary>Длительность, мс (для хранения у носителя); <see langword="null"/> — неизвестна.</summary>
    public long? DurationMs => Duration is { } duration && duration > TimeSpan.Zero
        ? (long)Math.Round(duration.TotalMilliseconds)
        : null;

    /// <summary>
    /// Номер последнего кадра записи по её длительности, или <see langword="null"/>, если длительность неизвестна.
    /// Последний кадр начинается не позже конца записи: <c>ceil(D · fps) − 1</c> (запись 2,0 с при 25 к/с — кадры
    /// 0..49; 2,02 с — 0..50), но не меньше нуля.
    /// </summary>
    public static long? LastFrameIndex(long? durationMs, double frameRate)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frameRate);
        if (durationMs is not { } duration || duration <= 0)
        {
            return null;
        }

        return Math.Max(0, (long)Math.Ceiling(duration * frameRate / 1000.0 - 1e-6) - 1);
    }

    /// <summary>Номер кадра (с нуля), показываемого в момент <paramref name="timestampMs"/>.</summary>
    public long FrameIndexAt(long timestampMs) => FrameIndexAt(timestampMs, FrameRate);

    /// <summary>Номер кадра (с нуля) в момент <paramref name="timestampMs"/> при частоте <paramref name="frameRate"/>.</summary>
    public static long FrameIndexAt(long timestampMs, double frameRate)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(timestampMs);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frameRate);
        return (long)Math.Round(timestampMs * frameRate / 1000.0);
    }

    /// <summary>
    /// Момент, с которого ffmpeg отдаёт ровно кадр <paramref name="frameIndex"/>: на полкадра раньше его
    /// номинального времени. Метки времени в контейнере округлены до его шага (у Matroska — 1 мс), и запрос ровно
    /// на <c>N/fps</c> у кадра с меткой, округлённой вниз, вернул бы кадр N+1; запас в полкадра заведомо больше
    /// любого округления (проверено 25.09.2026 на mp4/mkv/mov/avi/webm/3gp).
    /// </summary>
    public static TimeSpan SeekTimeFor(long frameIndex, double frameRate)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(frameIndex);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frameRate);
        return frameIndex == 0 ? TimeSpan.Zero : TimeSpan.FromSeconds((frameIndex - 0.5) / frameRate);
    }

    /// <summary>Номинальное время кадра <paramref name="frameIndex"/>, мс (для таймкода и ссылок <c>?t=</c>).</summary>
    public static long TimestampOf(long frameIndex, double frameRate)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(frameIndex);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frameRate);
        return (long)Math.Round(frameIndex * 1000.0 / frameRate);
    }
}

/// <summary>Формат изображения одного кадра, вырезаемого по времени.</summary>
public enum FrameImageFormat
{
    /// <summary>PNG без потерь — снимок кадра как новый носитель дела (ТЭ-007: кадр из оригинала без пересжатия).</summary>
    Png = 0,

    /// <summary>JPEG — покадровый просмотр с сервера (быстро, компактно; не для хранения).</summary>
    Jpeg = 1,
}
