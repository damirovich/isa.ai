using System;
using System.IO;
using System.Threading.Tasks;
using ISC.AI.Modules.Media.Domain.Model;
using ISC.AI.Vision.Onnx;
using ISC.AI.Vision.Onnx.Video;
using Shouldly;
using Xunit;

namespace ISC.AI.UnitTests.Vision;

/// <summary>
/// Вырезка кадра (ADR-0028) без ffmpeg: командная строка вывода (один кадр, кодек по формату, масштаб по
/// наибольшей стороне с чётными сторонами) и проверки аргументов до запуска процесса.
/// </summary>
public sealed class FfmpegFrameExtractorArgumentsTests
{
    [Fact(DisplayName = "PNG без предела стороны: один кадр, только видео, кодек png, БЕЗ фильтра масштаба (ТЭ-007 — кадр как есть)")]
    public void Png_without_max_side_has_no_scale_filter()
    {
        var arguments = FfmpegFrameExtractor.OutputArgumentsFor(FrameImageFormat.Png, maxSide: null);

        arguments.ShouldBe("-an -sn -dn -frames:v 1 -c:v png");
        arguments.ShouldNotContain("-vf");
    }

    [Fact(DisplayName = "JPEG с пределом 640: mjpeg качества 2 в полном диапазоне (ключ «за концом записи»), scale вписывает кадр по НАИБОЛЬШЕЙ стороне без увеличения, стороны чётные")]
    public void Jpeg_with_max_side_scales_by_larger_side()
    {
        var arguments = FfmpegFrameExtractor.OutputArgumentsFor(FrameImageFormat.Jpeg, maxSide: 640);

        arguments.ShouldStartWith("-an -sn -dn -frames:v 1 -c:v mjpeg -q:v 2 -color_range pc -strict unofficial -vf scale=");
        arguments.ShouldContain("w='min(640,iw)'");
        arguments.ShouldContain("h='min(640,ih)'");
        arguments.ShouldContain("force_original_aspect_ratio=decrease");
        arguments.ShouldContain("force_divisible_by=2");
    }

    [Fact(DisplayName = "Число в фильтре — инвариантная культура (без разделителей групп) и неизвестный формат — ошибка")]
    public void Numbers_are_invariant_and_unknown_format_is_rejected()
    {
        FfmpegFrameExtractor.OutputArgumentsFor(FrameImageFormat.Png, maxSide: 1920).ShouldContain("min(1920,iw)");
        Should.Throw<ArgumentOutOfRangeException>(() => FfmpegFrameExtractor.OutputArgumentsFor((FrameImageFormat)42, null));
    }

    [Fact(DisplayName = "Отрицательный момент, неположительный предел стороны и отсутствующий файл отвергаются ДО запуска ffmpeg")]
    public async Task Invalid_arguments_are_rejected_before_ffmpeg()
    {
        var extractor = new FfmpegFrameExtractor(new VisionOptions("d.onnx", "00", "e.onnx", "00", FfmpegFolder: "нет-такого-каталога"));
        var missing = Path.Combine(Path.GetTempPath(), "iscai-no-such-" + Guid.NewGuid().ToString("N") + ".mp4");

        await Should.ThrowAsync<ArgumentOutOfRangeException>(
            () => extractor.ExtractFrameAsync(missing, TimeSpan.FromSeconds(-1), FrameImageFormat.Png));
        await Should.ThrowAsync<ArgumentOutOfRangeException>(
            () => extractor.ExtractFrameAsync(missing, TimeSpan.Zero, FrameImageFormat.Jpeg, maxSide: 0));
        await Should.ThrowAsync<FileNotFoundException>(
            () => extractor.ExtractFrameAsync(missing, TimeSpan.Zero, FrameImageFormat.Png));
        await Should.ThrowAsync<FileNotFoundException>(() => extractor.ProbeAsync(missing));
    }
}
