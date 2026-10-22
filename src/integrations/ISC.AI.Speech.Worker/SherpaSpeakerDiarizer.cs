using System.Runtime.InteropServices;
using SherpaOnnx;

namespace ISC.AI.Speech.Worker;

/// <summary>Реплика говорящего: участок записи и номер диктора модели (с нуля, в порядке кластеров).</summary>
/// <param name="StartMs">Начало, мс от начала записи.</param>
/// <param name="EndMs">Конец, мс.</param>
/// <param name="Speaker">Номер диктора модели.</param>
internal sealed record SpeakerTurn(long StartMs, long EndMs, int Speaker);

/// <summary>
/// Разделение говорящих моделью (ADR-0062): sherpa-onnx <c>OfflineSpeakerDiarization</c> — сегментация pyannote (MIT)
/// и «голосовые отпечатки» CAM++ 3D-Speaker (Apache-2.0), кластеризация по числу дикторов или по порогу.
/// </summary>
/// <remarks>
/// Модели нужна ВСЯ запись в памяти (float, 4 байта на отсчёт 16 кГц — ~230 МБ на час), поэтому длину ограничивает
/// вызывающий (<see cref="WorkerArguments.SpeakerMaxMinutes"/>). Отказ загрузки — <see cref="ModelLoadException"/>, но
/// расшифровку он не роняет: разметка — дополнение к тексту, а не условие его выдачи.
/// </remarks>
internal sealed class SherpaSpeakerDiarizer : IDisposable
{
    private readonly OfflineSpeakerDiarization _diarization;

    private SherpaSpeakerDiarizer(OfflineSpeakerDiarization diarization) => _diarization = diarization;

    /// <summary>Загружает модели разделения говорящих; неудача — <see cref="ModelLoadException"/>.</summary>
    public static SherpaSpeakerDiarizer Load(WorkerArguments arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (arguments.SpeakerSegmentationPath is not { } segmentation || arguments.SpeakerEmbeddingPath is not { } embedding)
        {
            throw new ModelLoadException("Модели разделения говорящих не заданы.");
        }

        try
        {
            var config = new OfflineSpeakerDiarizationConfig();
            config.Segmentation.Pyannote.Model = segmentation;
            config.Segmentation.NumThreads = arguments.Threads;
            config.Segmentation.Provider = "cpu";
            config.Embedding.Model = embedding;
            config.Embedding.NumThreads = arguments.Threads;
            config.Embedding.Provider = "cpu";

            // Число дикторов известно (звонок — двое) — кластеров ровно столько; иначе — по порогу сходства.
            if (arguments.Speakers > 0)
            {
                config.Clustering.NumClusters = arguments.Speakers;
            }
            else
            {
                config.Clustering.NumClusters = -1;
                config.Clustering.Threshold = (float)arguments.SpeakerThreshold;
            }

            // Отбрасывать реплики короче 0,3 с и склеивать разрывы одного диктора короче 0,5 с — иначе на шуме
            // «дребезжит» смена дикторов посреди фразы.
            config.MinDurationOn = 0.3f;
            config.MinDurationOff = 0.5f;

            var diarization = new OfflineSpeakerDiarization(config);
            SherpaSpeechRecognizer.EnsureNativeHandle(diarization, "разделение говорящих", segmentation);
            return new SherpaSpeakerDiarizer(diarization);
        }
        catch (ModelLoadException)
        {
            throw;
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException
                                              or SEHException or ExternalException or InvalidOperationException)
        {
            throw new ModelLoadException(
                $"Модели разделения говорящих не загрузились: {exception.GetType().Name}: {exception.Message.TrimEnd('.')}. "
                + "Проверьте файлы моделей и их пины SHA-256.", exception);
        }
    }

    /// <summary>Читает всю запись и размечает реплики; результат — по времени начала.</summary>
    public IReadOnlyList<SpeakerTurn> Run(string wavPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(wavPath);
        using var reader = Pcm16WaveReader.Open(new FileStream(
            wavPath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan));

        var samples = new List<float>(reader.ExpectedSamples is { } expected && expected < int.MaxValue ? (int)expected : 16000 * 60);
        var buffer = new float[16000];
        int read;
        while ((read = reader.Read(buffer)) > 0)
        {
            samples.AddRange(buffer.AsSpan(0, read));
        }

        var segments = _diarization.Process(samples.ToArray());
        return segments
            .Select(s => new SpeakerTurn((long)Math.Round(s.Start * 1000), (long)Math.Round(s.End * 1000), s.Speaker))
            .Where(t => t.EndMs > t.StartMs)
            .OrderBy(t => t.StartMs)
            .ToList();
    }

    /// <inheritdoc />
    public void Dispose() => _diarization.Dispose();
}
