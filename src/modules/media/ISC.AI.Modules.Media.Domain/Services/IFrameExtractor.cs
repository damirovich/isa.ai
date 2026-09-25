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
    /// <summary>
    /// Извлекает кадры видеофайла <paramref name="videoPath"/> с частотой из <paramref name="sampling"/>.
    /// В файле без видеопотока (только звук) кадров нет — поток пуст, а не ошибка.
    /// </summary>
    IAsyncEnumerable<VideoFrame> ExtractAsync(
        string videoPath, FrameSamplingOptions sampling, CancellationToken cancellationToken = default);

    /// <summary>
    /// Есть ли в файле видеопоток (картинка). Обложка звуковой записи (прикреплённое изображение) видеопотоком
    /// не считается.
    /// </summary>
    /// <remarks>
    /// Нужна до раскадровки (ADR-0026): браузер объявляет голосовое <c>.3gp</c> как <c>video/3gpp</c>, звук в
    /// mp4/webm — тоже как видео. У такого носителя поиск по лицу неприменим, и конвейер лиц переводит его в
    /// аудиозаписи, не записывая в журнал «индексацию биометрии», которой не было.
    /// </remarks>
    /// <exception cref="InvalidOperationException">Внешний инструмент не найден или не разобрал файл.</exception>
    Task<bool> HasVideoStreamAsync(string videoPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Проба видеопотока (ADR-0028): нативная частота кадров, длительность, размер кадра после автоповорота.
    /// <see langword="null"/> — видеопотока нет (только звук или обложка).
    /// </summary>
    /// <exception cref="InvalidOperationException">Внешний инструмент не найден или не разобрал файл.</exception>
    Task<VideoProbe?> ProbeAsync(string videoPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Вырезает ОДИН кадр в момент <paramref name="at"/> из оригинала: точный поиск (не ближайший ключевой кадр),
    /// автоповорот по метке контейнера, без иной обработки (ТЭ-007). Момент для кадра № N —
    /// <see cref="VideoProbe.SeekTimeFor"/>.
    /// </summary>
    /// <param name="videoPath">Путь к файлу (оригинал на месте или временная копия).</param>
    /// <param name="at">Момент записи.</param>
    /// <param name="format">PNG (снимок, без потерь) или JPEG (просмотр).</param>
    /// <param name="maxSide">Наибольшая сторона результата, пиксели: кадр крупнее уменьшается с сохранением
    /// пропорций (просмотр); <see langword="null"/> — исходный размер (снимок).</param>
    /// <param name="cancellationToken">Отмена останавливает внешний процесс.</param>
    /// <returns>Байты изображения; <see langword="null"/> — момент за концом записи (кадра нет).</returns>
    /// <exception cref="InvalidOperationException">Внешний инструмент не найден, файл без видеопотока или не разобран.</exception>
    Task<byte[]?> ExtractFrameAsync(
        string videoPath, TimeSpan at, FrameImageFormat format, int? maxSide = null,
        CancellationToken cancellationToken = default);
}
