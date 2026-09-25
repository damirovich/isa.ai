using System;
using System.Threading.Tasks;
using ISC.AI.Modules.Media.Domain.Model;
using ISC.AI.Vision.Onnx;
using ISC.AI.Vision.Onnx.Video;
using Shouldly;
using Xunit;

namespace ISC.AI.UnitTests.Vision;

/// <summary>
/// Страховка раскадровщика: путь, который разорвал бы командную строку ffmpeg/ffprobe (FFMpegCore передаёт его
/// текстом в кавычках без экранирования), отклоняется ДО запуска внешнего процесса — во всех четырёх входах:
/// проба потоков, раскадровка, проба видео и вырезка кадра (ADR-0028). Бинарник ffmpeg не нужен — до него дело
/// не доходит.
/// </summary>
public sealed class FfmpegFrameExtractorPathTests
{
    [Theory(DisplayName = "Путь с кавычкой или с «\\» в конце → ArgumentException в пробе потоков, раскадровке, пробе видео и вырезке кадра")]
    [InlineData("C:\\temp\\a.mp4\" -y \"out")]
    [InlineData("/tmp/iscai-media/frames-0a.m4a\" -i \"/etc/passwd")]
    [InlineData("C:\\temp\\frames-0a.mp4\\")]
    public async Task Unsafe_path_is_rejected_before_ffmpeg(string path)
    {
        var extractor = new FfmpegFrameExtractor(new VisionOptions("d.onnx", "00", "e.onnx", "00", FfmpegFolder: "нет-такого-каталога"));

        await Should.ThrowAsync<ArgumentException>(() => extractor.HasVideoStreamAsync(path));
        await Should.ThrowAsync<ArgumentException>(() => extractor.ProbeAsync(path));
        await Should.ThrowAsync<ArgumentException>(() => extractor.ExtractFrameAsync(path, TimeSpan.FromSeconds(1), FrameImageFormat.Png));
        await Should.ThrowAsync<ArgumentException>(async () =>
        {
            await foreach (var _ in extractor.ExtractAsync(path, new FrameSamplingOptions(1.0)))
            {
                // до первой итерации дело не доходит
            }
        });
    }
}
