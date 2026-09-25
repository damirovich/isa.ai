using System.Runtime.CompilerServices;
using System.Threading.Channels;
using FFMpegCore;
using FFMpegCore.Pipes;
using ISC.AI.Modules.Media.Domain.Model;
using ISC.AI.Modules.Media.Domain.Services;

namespace ISC.AI.Vision.Onnx.Video;

/// <summary>
/// Раскадровка видео внешним процессом ffmpeg (LGPL-сборка, без линковки; обёртка FFMpegCore — MIT,
/// ADR-0020): <c>-vf fps=N -f image2pipe -c:v mjpeg</c> в stdout, поток режется на кадры по мере
/// поступления и отдаётся через канал — длинное видео в памяти не собирается (ТО-мат-06).
/// </summary>
/// <remarks>
/// Таймкод кадра — <c>индекс / fps</c>: фильтр fps выдаёт кадры с постоянным шагом. Отсутствие
/// ffmpeg — явная ошибка при первом обращении (бинарник поставляется в дистрибутиве, ТИ-004).
/// Перед раскадровкой потоки файла проверяются ffprobe (<see cref="HasVideoStreamAsync"/>): в файле без
/// видеопотока (голосовое .3gp, звук в mp4/webm) ffmpeg не создал бы ни одного выходного потока и упал бы —
/// вместо этого кадров просто нет (ADR-0026; основная проверка — в индексаторе, здесь второй рубеж).
/// </remarks>
public sealed class FfmpegFrameExtractor(VisionOptions options) : IFrameExtractor
{
    /// <summary>Предел длины сообщения ffprobe в тексте ошибки (статус носителя — не место для журнала ffmpeg).</summary>
    private const int MaxToolMessageLength = 1000;

    /// <inheritdoc />
    public async IAsyncEnumerable<VideoFrame> ExtractAsync(
        string videoPath, FrameSamplingOptions sampling,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sampling);
        EnsureSafePath(videoPath);
        if (!File.Exists(videoPath))
        {
            throw new FileNotFoundException($"Видеофайл не найден: {videoPath}", videoPath);
        }

        var ffOptions = FfOptions();

        // Второй рубеж ADR-0026: без видеопотока — ноль кадров, а не сбой ffmpeg «нет выходных потоков».
        if (!await HasVideoStreamCoreAsync(videoPath, ffOptions, cancellationToken))
        {
            yield break;
        }

        var fps = Math.Clamp(sampling.FramesPerSecond, 0.1, 30);
        var channel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(16) { SingleWriter = true, SingleReader = true });
        var sink = new ChannelSink(channel.Writer);

        var run = Task.Run(async () =>
        {
            try
            {
                await FFMpegArguments
                    .FromFileInput(videoPath)
                    .OutputToPipe(sink, o => o
                        .WithVideoCodec("mjpeg")
                        .ForceFormat("image2pipe")
                        .WithCustomArgument($"-vf fps={fps.ToString(System.Globalization.CultureInfo.InvariantCulture)} -q:v 3"))
                    .CancellableThrough(cancellationToken)
                    .ProcessAsynchronously(true, ffOptions);
                channel.Writer.TryComplete();
            }
            catch (Exception exception)
            {
                channel.Writer.TryComplete(exception);
            }
        }, cancellationToken);

        var index = 0;
        await foreach (var jpeg in channel.Reader.ReadAllAsync(cancellationToken))
        {
            if (sampling.MaxFrames is { } max && index >= max)
            {
                break;
            }

            yield return new VideoFrame(index, TimeSpan.FromSeconds(index / fps), jpeg);
            index++;
        }

        await run; // пробрасывает ошибку ffmpeg (например, «бинарник не найден») наружу
    }

    /// <inheritdoc />
    public Task<bool> HasVideoStreamAsync(string videoPath, CancellationToken cancellationToken = default)
    {
        EnsureSafePath(videoPath);
        if (!File.Exists(videoPath))
        {
            throw new FileNotFoundException($"Видеофайл не найден: {videoPath}", videoPath);
        }

        return HasVideoStreamCoreAsync(videoPath, FfOptions(), cancellationToken);
    }

    /// <summary>
    /// Отказ ДО запуска внешнего процесса, если путь разорвал бы его командную строку: FFMpegCore передаёт путь
    /// текстом в кавычках без экранирования, и кавычка в пути или «\» в его конце (экранирует закрывающую
    /// кавычку) превращаются в лишние аргументы ffmpeg/ffprobe — посторонний вход или выход. Имена временных
    /// копий конвейер строит сам (GUID и отфильтрованное расширение), так что здесь это страховка.
    /// </summary>
    private static void EnsureSafePath(string videoPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(videoPath);
        if (videoPath.Contains('"', StringComparison.Ordinal) || videoPath.EndsWith('\\'))
        {
            throw new ArgumentException(
                "Путь к видеофайлу содержит кавычку или оканчивается обратной косой чертой: внешний процесс ffmpeg "
                + "получил бы лишние аргументы. Файл не обрабатывается.",
                nameof(videoPath));
        }
    }

    /// <summary>
    /// Проба потоков ffprobe. Видеопоток — любой, кроме прикреплённого изображения (обложка звуковой записи
    /// видеопотоком для ffprobe является, но картинки во времени у неё нет).
    /// </summary>
    private static async Task<bool> HasVideoStreamCoreAsync(string videoPath, FFOptions ffOptions, CancellationToken cancellationToken)
    {
        IMediaAnalysis analysis;
        try
        {
            analysis = await FFProbe.AnalyseAsync(videoPath, ffOptions, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException("ffprobe не разобрал файл: " + Shorten(exception.Message.Trim()), exception);
        }

        return analysis.VideoStreams.Exists(stream => !IsAttachedPicture(stream));
    }

    private static bool IsAttachedPicture(VideoStream stream) =>
        stream.Disposition is { } disposition
        && disposition.TryGetValue("attached_pic", out var attached)
        && attached;

    private FFOptions FfOptions()
    {
        var ffOptions = new FFOptions();
        if (!string.IsNullOrWhiteSpace(options.FfmpegFolder))
        {
            ffOptions.BinaryFolder = options.FfmpegFolder;
        }

        return ffOptions;
    }

    // Сообщение ffprobe бывает многострочным и длинным; статусу носителя нужна суть, а не весь журнал.
    private static string Shorten(string message) =>
        message.Length <= MaxToolMessageLength ? message : message[..MaxToolMessageLength] + "…";

    // Приёмник stdout ffmpeg: читает поток и режет на кадры прямо по мере поступления.
    private sealed class ChannelSink(ChannelWriter<byte[]> writer) : IPipeSink
    {
        public string GetFormat() => "image2pipe";

        public async Task ReadAsync(Stream inputStream, CancellationToken cancellationToken)
        {
            using var splitter = new MjpegStreamSplitter();
            var buffer = new byte[64 * 1024];
            int read;
            while ((read = await inputStream.ReadAsync(buffer, cancellationToken)) > 0)
            {
                foreach (var frame in splitter.Push(buffer.AsSpan(0, read)))
                {
                    await writer.WriteAsync(frame, cancellationToken);
                }
            }
        }
    }
}
