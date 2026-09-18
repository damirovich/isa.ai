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
/// </remarks>
public sealed class FfmpegFrameExtractor(VisionOptions options) : IFrameExtractor
{
    /// <inheritdoc />
    public async IAsyncEnumerable<VideoFrame> ExtractAsync(
        string videoPath, FrameSamplingOptions sampling,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sampling);
        if (!File.Exists(videoPath))
        {
            throw new FileNotFoundException($"Видеофайл не найден: {videoPath}", videoPath);
        }

        var fps = Math.Clamp(sampling.FramesPerSecond, 0.1, 30);
        var channel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(16) { SingleWriter = true, SingleReader = true });
        var sink = new ChannelSink(channel.Writer);

        var ffOptions = new FFOptions();
        if (!string.IsNullOrWhiteSpace(options.FfmpegFolder))
        {
            ffOptions.BinaryFolder = options.FfmpegFolder;
        }

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
