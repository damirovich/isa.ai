using ISC.AI.Modules.Media.Domain.Model;

namespace ISC.AI.Modules.Media.Domain.Services;

/// <summary>
/// Порт построения волны аудиозаписи (ADR-0047): файл → огибающая громкости (<see cref="AudioWaveform"/>).
/// Звук не меняется (ТЭ-007) — волна только картинка под проигрывателем.
/// </summary>
/// <remarks>
/// Реализация — внешний процесс ffmpeg (LGPL-сборка, обёртка MIT, ADR-0020), как у вырезки кадра (ADR-0028):
/// отсутствие бинарника — явная ошибка, не пустая волна. Число одновременных процессов ограничено, прогон — по
/// таймауту.
/// </remarks>
public interface IAudioWaveformExtractor
{
    /// <summary>Строит волну файла <paramref name="audioPath"/> (оригинал на месте или временная копия).</summary>
    /// <param name="audioPath">Путь к файлу записи.</param>
    /// <param name="cancellationToken">Отмена останавливает внешний процесс.</param>
    /// <exception cref="InvalidOperationException">
    /// Внешний инструмент не найден, файл без звука или не разобран, сервер занят либо прогон не уложился в таймаут.
    /// </exception>
    Task<AudioWaveform> ExtractAsync(string audioPath, CancellationToken cancellationToken = default);
}

/// <summary>
/// Кэш волны в строке носителя (ADR-0047): волна строится один раз и хранится как производная носителя (как лента
/// кадров видео, ADR-0038) — без режимных полей: читается только после проверки допуска к самому носителю.
/// </summary>
public interface IAudioWaveformCache
{
    /// <summary>Сохранённая волна носителя ТЕКУЩЕЙ версии алгоритма; <see langword="null"/> — нет или устарела.</summary>
    Task<AudioWaveform?> GetAsync(int assetId, CancellationToken cancellationToken = default);

    /// <summary>Запомнить волну носителя (перезаписывает прежнюю); носителя нет — ничего не делает.</summary>
    Task SaveAsync(int assetId, AudioWaveform waveform, CancellationToken cancellationToken = default);
}
