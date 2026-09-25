using ISC.AI.Modules.Media.Domain.Model;
using ISC.AI.Modules.Media.Domain.Services;

namespace ISC.AI.Speech;

/// <summary>
/// Реализация порта <see cref="IAudioTranscriber"/> для поставки, где расшифровка НЕ настроена (нет путей
/// или пинов в секции <c>Speech</c>): каждая попытка — явный отказ «Расшифровка не настроена: …» с перечнем
/// недостающих ключей (ADR-0026).
/// </summary>
/// <remarks>
/// ПОЧЕМУ ОТКАЗ, А НЕ «НОЛЬ ФРАГМЕНТОВ». Пустая расшифровка выглядела бы как «в записи нет речи», и поиск
/// по делу молча не находил бы сказанного — следователь не узнал бы, что запись вообще не
/// обрабатывалась. Тот же приём, что у OCR («OCR не настроен»): хост стартует и без модели (фото, видео,
/// документы работают), а неготовая функция честно говорит о себе при первом обращении.
/// </remarks>
public sealed class UnconfiguredAudioTranscriber : IAudioTranscriber
{
    /// <summary>Значение <see cref="ModelVersion"/>, пока распознавание не настроено.</summary>
    public const string NotConfiguredModelVersion = "not-configured";

    /// <summary>Создаёт отказ с перечнем недостающих ключей.</summary>
    /// <param name="missingKeys">Незаданные обязательные ключи (см. <see cref="SpeechOptions.MissingKeys"/>).</param>
    public UnconfiguredAudioTranscriber(IReadOnlyList<string> missingKeys)
    {
        ArgumentNullException.ThrowIfNull(missingKeys);
        Reason = "Расшифровка не настроена: не заданы ключи конфигурации "
            + (missingKeys.Count == 0 ? "секции Speech" : string.Join(", ", missingKeys))
            + ". Файлы модели и детектора речи с пинами SHA-256 — deploy/offline/export-speech-models.ps1"
            + " (пины — из манифеста speech-models.sha256), процесс-распознаватель — deploy/offline/publish-speech-worker.ps1 (ADR-0026).";
    }

    /// <summary>Текст отказа — тот же, что в исключении.</summary>
    public string Reason { get; }

    /// <inheritdoc />
    public string ModelVersion => NotConfiguredModelVersion;

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">Всегда: расшифровка не настроена (в задаче, а не при вызове).</exception>
    public Task<AudioTranscription> TranscribeAsync(string sourcePath, CancellationToken cancellationToken = default) =>
        cancellationToken.IsCancellationRequested
            ? Task.FromCanceled<AudioTranscription>(cancellationToken)
            // Отказ — в возвращённой задаче, как у рабочей реализации (async-метод бросает при ожидании):
            // вызывающему коду не нужно различать «бросил при вызове» и «бросил при ожидании».
            : Task.FromException<AudioTranscription>(new InvalidOperationException(Reason));
}
