using System.Threading;
using System.Threading.Tasks;
using ISC.AI.Modules.Media.Domain.Model;

namespace ISC.AI.Modules.Media.Domain.Services;

/// <summary>
/// Распознавание речи (ADR-0026): аудио- или видеофайл → фрагменты текста с таймкодами. Реализация —
/// интеграция <c>ISC.AI.Speech</c> (ffmpeg → детектор речи → модель распознавания во внешнем процессе).
/// </summary>
/// <remarks>
/// ПОЧЕМУ ПОРТ, А НЕ ПРЯМОЙ ВЫЗОВ МОДЕЛИ. Выбор модели (GigaAM Multilingual) сделан по опубликованным
/// метрикам, но на русско-киргизской смешанной речи и записях заказчика его ещё предстоит подтвердить
/// пилотом. Если пилот покажет, что лучше другая модель (Vosk, Omnilingual, Whisper для чисто русских
/// записей), меняется реализация порта — конвейер, хранение, поиск и интерфейс остаются как есть.
/// Тот же приём, что у распознавания лиц (<see cref="IFaceDetector"/>, <see cref="IFaceEmbedder"/>).
///
/// Реализация работает ТОЛЬКО локально: модели загружаются по пути с проверкой SHA-256, сеть не
/// используется (изолированный контур).
/// </remarks>
public interface IAudioTranscriber
{
    /// <summary>
    /// Версия модели и её пина — пишется в расшифровку и журнал, чтобы любой текст можно было отнести к
    /// конкретной модели (результаты разных моделей не сравнимы).
    /// </summary>
    string ModelVersion { get; }

    /// <summary>
    /// Расшифровывает файл. Звук извлекается из любого поддерживаемого контейнера (аудио или видео),
    /// приводится к формату модели и режется по паузам.
    /// </summary>
    /// <remarks>
    /// Итог отдаётся ЦЕЛИКОМ после прогона, а не потоком: конвейер записывает расшифровку одной транзакцией
    /// (частичный результат сбойного прогона не сохраняется), а длительность записи известна только в конце.
    /// </remarks>
    /// <param name="sourcePath">Путь к локальному файлу (временная копия исходника носителя).</param>
    /// <param name="cancellationToken">Отмена прекращает распознавание и останавливает внешний процесс.</param>
    /// <returns>Фрагменты и длительность записи; без звуковой дорожки — <see cref="AudioTranscription.NoAudio"/>.</returns>
    /// <exception cref="InvalidOperationException">Распознавание не настроено, модель не прошла проверку или прогон не удался.</exception>
    Task<AudioTranscription> TranscribeAsync(string sourcePath, CancellationToken cancellationToken = default);
}

/// <summary>
/// Исполнитель конвейера расшифровки носителя (ADR-0026): вызывается фоновой очередью ядра; повторный
/// запуск идемпотентен (расшифровка носителя перезаписывается целиком).
/// </summary>
public interface IMediaTranscriptionPipeline
{
    /// <summary>Расшифровать носитель; ошибка фиксируется в статусе носителя и возвращается, не бросается.</summary>
    Task<MediaTranscriptionResult> TranscribeAsync(int assetId, CancellationToken cancellationToken = default);
}
