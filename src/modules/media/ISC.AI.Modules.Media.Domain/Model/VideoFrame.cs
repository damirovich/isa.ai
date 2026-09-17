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
