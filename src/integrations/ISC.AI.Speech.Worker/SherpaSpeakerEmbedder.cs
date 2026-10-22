using System.Runtime.InteropServices;
using SherpaOnnx;

namespace ISC.AI.Speech.Worker;

/// <summary>Участок записи реплики, мс от начала.</summary>
/// <param name="StartMs">Начало.</param>
/// <param name="EndMs">Конец.</param>
internal readonly record struct SegmentSpan(long StartMs, long EndMs);

/// <summary>
/// «Голосовые отпечатки» реплик (ADR-0062 v0.3): sherpa-onnx <c>SpeakerEmbeddingExtractor</c> на модели CAM++ 3D-Speaker
/// (Apache-2.0) — по одному вектору на реплику распознавателя. Группирует их <see cref="SpeakerClustering"/>.
/// </summary>
/// <remarks>
/// Запись читается потоком, по порядку реплик: в памяти — только звук текущей реплики (не вся запись, как у прежней
/// <c>OfflineSpeakerDiarization</c>). Отказ загрузки — <see cref="ModelLoadException"/>; расшифровку он не роняет:
/// разметка — дополнение к тексту, а не условие его выдачи.
/// </remarks>
internal sealed class SherpaSpeakerEmbedder : IDisposable
{
    private readonly SpeakerEmbeddingExtractor _extractor;

    private SherpaSpeakerEmbedder(SpeakerEmbeddingExtractor extractor) => _extractor = extractor;

    /// <summary>Загружает модель отпечатков; неудача — <see cref="ModelLoadException"/>.</summary>
    public static SherpaSpeakerEmbedder Load(WorkerArguments arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (arguments.SpeakerEmbeddingPath is not { } model)
        {
            throw new ModelLoadException("Модель «голосовых отпечатков» не задана.");
        }

        try
        {
            var config = new SpeakerEmbeddingExtractorConfig { Model = model, NumThreads = arguments.Threads, Provider = "cpu" };
            var extractor = new SpeakerEmbeddingExtractor(config);
            SherpaSpeechRecognizer.EnsureNativeHandle(extractor, "модель «голосовых отпечатков»", model);
            return new SherpaSpeakerEmbedder(extractor);
        }
        catch (ModelLoadException)
        {
            throw;
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException
                                              or SEHException or ExternalException or InvalidOperationException)
        {
            throw new ModelLoadException(
                $"Модель «голосовых отпечатков» не загрузилась: {exception.GetType().Name}: {exception.Message.TrimEnd('.')}. "
                + "Проверьте файл модели и его пин SHA-256.", exception);
        }
    }

    /// <summary>
    /// Отпечатки реплик по порядку; <see langword="null"/> — реплика слишком коротка для модели. Реплики идут по
    /// времени (так их пишет распознаватель); перекрытие с предыдущей обрезается.
    /// </summary>
    public IReadOnlyList<float[]?> Compute(string wavPath, IReadOnlyList<SegmentSpan> spans)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(wavPath);
        ArgumentNullException.ThrowIfNull(spans);
        using var reader = Pcm16WaveReader.Open(new FileStream(
            wavPath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan));

        const int samplesPerMs = Pcm16WaveReader.RequiredSampleRate / 1000;
        var scratch = new float[Pcm16WaveReader.RequiredSampleRate];
        var position = 0L;
        var result = new List<float[]?>(spans.Count);
        foreach (var span in spans)
        {
            var start = Math.Max(span.StartMs * samplesPerMs, position);
            var end = span.EndMs * samplesPerMs;
            Skip(reader, scratch, start - position);
            position = start;

            var length = (int)Math.Max(0, end - start);
            var samples = new float[length];
            var filled = Fill(reader, scratch, samples);
            position += filled;
            result.Add(filled > 0 ? Embed(filled == length ? samples : samples[..filled]) : null);
        }

        return result;
    }

    private float[]? Embed(float[] samples)
    {
        using var stream = _extractor.CreateStream();
        stream.AcceptWaveform(Pcm16WaveReader.RequiredSampleRate, samples);
        stream.InputFinished();
        return _extractor.IsReady(stream) ? _extractor.Compute(stream) : null;
    }

    private static void Skip(Pcm16WaveReader reader, float[] scratch, long count)
    {
        while (count > 0)
        {
            var read = reader.Read(count >= scratch.Length ? scratch : new float[count]);
            if (read == 0)
            {
                return;
            }

            count -= read;
        }
    }

    private static int Fill(Pcm16WaveReader reader, float[] scratch, float[] target)
    {
        var filled = 0;
        while (filled < target.Length)
        {
            var wanted = Math.Min(scratch.Length, target.Length - filled);
            var buffer = wanted == scratch.Length ? scratch : new float[wanted];
            var read = reader.Read(buffer);
            if (read == 0)
            {
                break;
            }

            Array.Copy(buffer, 0, target, filled, read);
            filled += read;
        }

        return filled;
    }

    /// <inheritdoc />
    public void Dispose() => _extractor.Dispose();
}
