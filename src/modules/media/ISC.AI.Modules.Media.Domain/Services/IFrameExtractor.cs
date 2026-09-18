using ISC.AI.Modules.Media.Domain.Model;

namespace ISC.AI.Modules.Media.Domain.Services;

/// <summary>
/// Порт раскадровки видео (ТО-мат-06): файл → поток кадров с таймкодами. Кадры отдаются
/// по мере извлечения (длинное видео не собирается в памяти целиком); каждый кадр — JPEG,
/// который дальше идёт в <see cref="IFaceDetector"/> тем же путём, что фотография.
/// </summary>
/// <remarks>
/// Реализация — внешний процесс ffmpeg (LGPL-сборка) через обёртку MIT (ADR-0020): бинарник
/// поставляется в дистрибутиве; отсутствие — явная ошибка при первом обращении, не пустой поток.
/// </remarks>
public interface IFrameExtractor
{
    /// <summary>Извлекает кадры видеофайла <paramref name="videoPath"/> с частотой из <paramref name="sampling"/>.</summary>
    IAsyncEnumerable<VideoFrame> ExtractAsync(
        string videoPath, FrameSamplingOptions sampling, CancellationToken cancellationToken = default);
}
