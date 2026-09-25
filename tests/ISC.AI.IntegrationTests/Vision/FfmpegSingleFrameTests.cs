using System;
using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ISC.AI.Modules.Media.Domain.Model;
using ISC.AI.Vision.Onnx;
using ISC.AI.Vision.Onnx.Video;
using Shouldly;
using SkiaSharp;
using Xunit;

namespace ISC.AI.IntegrationTests.Vision;

/// <summary>
/// Проба видео и вырезка одного кадра НАСТОЯЩИМ ffmpeg (ADR-0028, предлагаемые ТФ-МЕД-11/12): частота, длительность
/// и размер после автоповорота; кадр № N по моменту <see cref="VideoProbe.SeekTimeFor"/> ПОБАЙТНО совпадает с
/// кадром N эталонного декодирования во всех контейнерах пакета (mp4, mkv, mov, avi, webm, 3gp), в том числе
/// NTSC 29,97, при повороте и при ненулевом стартовом времени файла; PNG/JPEG, предел стороны, момент за концом.
/// Источник — <c>testsrc</c> ffmpeg: рисует счётчик кадров, поэтому соседние кадры заведомо различны.
/// </summary>
/// <remarks>Поставка ffmpeg — см. <see cref="VideoTestEnvironment"/>; без неё тесты падают с инструкцией.</remarks>
[Trait("Category", "Video")]
public sealed class FfmpegSingleFrameTests : IAsyncLifetime
{
    private const int ClipSeconds = 2;
    private const int ClipFps = 25;
    private const int ClipWidth = 320;
    private const int ClipHeight = 240;

    /// <summary>Стартовое время клипа с ненулевым отсчётом, с (<c>-output_ts_offset</c>).</summary>
    private const int OffsetSeconds = 10;

    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private string _ffmpegFolder = string.Empty;
    private string _workDir = string.Empty;

    /// <summary>
    /// Клипы: 320×240 при 25 к/с в mp4/mkv/mov (H.264 через Media Foundation), avi (MPEG-4 part 2), 3gp (H.263,
    /// 176×144 — единственный размер кодека), webm (VP9, NTSC 30000/1001); повёрнутая копия mp4 (метка контейнера
    /// 90°, поток не перекодируется); mkv со стартовым временем 10 с; 1920×1080 для предела стороны; звук без
    /// картинки (m4a); незавершённые mkv/webm без длительности (<c>-live 1</c>).
    /// </summary>
    public async Task InitializeAsync()
    {
        _ffmpegFolder = VideoTestEnvironment.LocateFfmpegFolder();
        _workDir = VideoTestEnvironment.CreateWorkDir();

        var source = $"-f lavfi -i testsrc=size={ClipWidth}x{ClipHeight}:rate={ClipFps} -t {ClipSeconds}";
        await RunAsync($"-y {source} -c:v h264_mf \"{Clip("mp4")}\"");
        await RunAsync($"-y {source} -c:v h264_mf \"{Clip("mkv")}\"");
        await RunAsync($"-y {source} -c:v h264_mf \"{Clip("mov")}\"");
        await RunAsync($"-y {source} -c:v mpeg4 -q:v 5 \"{Clip("avi")}\"");
        await RunAsync($"-y -f lavfi -i testsrc=size=176x144:rate={ClipFps} -t {ClipSeconds} -c:v h263 \"{Clip("3gp")}\"");
        await RunAsync($"-y -f lavfi -i testsrc=size={ClipWidth}x{ClipHeight}:rate=30000/1001 -t {ClipSeconds} -c:v libvpx-vp9 -b:v 300k \"{Clip("webm")}\"");

        // Поворот — только метка контейнера (display matrix), как у телефонной съёмки; кадры те же.
        await RunAsync($"-y -display_rotation 90 -i \"{Clip("mp4")}\" -c copy \"{Rotated}\"");
        await RunAsync($"-y {source} -output_ts_offset {OffsetSeconds} -c:v h264_mf \"{Offset}\"");
        await RunAsync($"-y -f lavfi -i testsrc=size=1920x1080:rate={ClipFps} -t 1 -c:v h264_mf \"{Big}\"");
        await RunAsync($"-y -f lavfi -i sine=frequency=440:duration=2 -c:a aac -b:a 32k \"{AudioOnly}\"");

        // Незавершённая запись (оборванная по питанию / поток MediaRecorder): -live 1 — муксер не возвращается к
        // заголовку, и ни контейнер, ни потоки длительности не сообщают (ffprobe поле duration опускает).
        await RunAsync($"-y {source} -c:v h264_mf -live 1 \"{Live("mkv")}\"");
        await RunAsync($"-y {source} -c:v libvpx-vp9 -b:v 300k -live 1 \"{Live("webm")}\"");
    }

    /// <inheritdoc />
    public Task DisposeAsync()
    {
        VideoTestEnvironment.TryDeleteWorkDir(_workDir);
        return Task.CompletedTask;
    }

    [Fact(DisplayName = "ProbeAsync: mp4 25 к/с — частота 25, длительность клипа, 320×240; webm 30000/1001 — ≈29,97; mkv без длительности потока — длительность из контейнера")]
    public async Task Probe_reports_native_frame_rate_duration_and_size()
    {
        var extractor = Extractor();

        var mp4 = (await extractor.ProbeAsync(Clip("mp4"))).ShouldNotBeNull();
        mp4.FrameRate.ShouldBe(ClipFps);
        mp4.Duration!.Value.TotalSeconds.ShouldBe(ClipSeconds, tolerance: 0.1);
        mp4.DurationMs!.Value.ShouldBeInRange(ClipSeconds * 1000 - 100, ClipSeconds * 1000 + 100);
        mp4.Width.ShouldBe(ClipWidth);
        mp4.Height.ShouldBe(ClipHeight);

        var webm = (await extractor.ProbeAsync(Clip("webm"))).ShouldNotBeNull();
        webm.FrameRate.ShouldBe(30000.0 / 1001, tolerance: 0.001);
        webm.Duration!.Value.TotalSeconds.ShouldBe(ClipSeconds, tolerance: 0.1);

        // У Matroska длительность потока «N/A» — берётся длительность контейнера.
        var mkv = (await extractor.ProbeAsync(Clip("mkv"))).ShouldNotBeNull();
        mkv.FrameRate.ShouldBe(ClipFps);
        mkv.Duration!.Value.TotalSeconds.ShouldBe(ClipSeconds, tolerance: 0.1);

        var small = (await extractor.ProbeAsync(Clip("3gp"))).ShouldNotBeNull();
        small.Width.ShouldBe(176);
        small.Height.ShouldBe(144);
    }

    [Theory(DisplayName = "ProbeAsync: незавершённый mkv/webm без длительности — Duration и DurationMs null, частота и размер есть; кадры вырезаются, за концом — null")]
    [InlineData("mkv")]
    [InlineData("webm")]
    public async Task Unfinished_recording_has_null_duration_but_frames(string extension)
    {
        var extractor = Extractor();

        var probe = (await extractor.ProbeAsync(Live(extension))).ShouldNotBeNull();
        probe.Duration.ShouldBeNull();
        probe.DurationMs.ShouldBeNull();
        probe.FrameRate.ShouldBe(ClipFps);
        probe.Width.ShouldBe(ClipWidth);
        probe.Height.ShouldBe(ClipHeight);

        await AssertFrameMatchesReferenceAsync(Live(extension), 37);
        (await extractor.ExtractFrameAsync(Live(extension), TimeSpan.FromSeconds(10), FrameImageFormat.Png)).ShouldBeNull();
    }

    [Fact(DisplayName = "ProbeAsync: метка поворота 90° — ширина и высота поменяны местами (размер ПОСЛЕ автоповорота); звук без картинки — null")]
    public async Task Probe_swaps_sides_for_rotated_clip_and_returns_null_without_video()
    {
        var extractor = Extractor();

        var rotated = (await extractor.ProbeAsync(Rotated)).ShouldNotBeNull();
        rotated.Width.ShouldBe(ClipHeight);
        rotated.Height.ShouldBe(ClipWidth);
        rotated.FrameRate.ShouldBe(ClipFps);

        (await extractor.ProbeAsync(AudioOnly)).ShouldBeNull();
        (await extractor.HasVideoStreamAsync(AudioOnly)).ShouldBeFalse();
    }

    [Theory(DisplayName = "ExtractFrameAsync: PNG кадра № N по SeekTimeFor побайтно равен кадру N эталонного декодирования — mp4/mkv/mov/avi/3gp (25) и webm (29,97)")]
    [InlineData("mp4", 0)]
    [InlineData("mp4", 37)]
    [InlineData("mkv", 37)]
    [InlineData("mkv", 49)]
    [InlineData("mov", 12)]
    [InlineData("avi", 0)]
    [InlineData("avi", 37)]
    [InlineData("3gp", 37)]
    [InlineData("webm", 1)]
    [InlineData("webm", 37)]
    public async Task Png_frame_equals_reference_frame_in_every_container(string extension, int frameIndex)
    {
        await AssertFrameMatchesReferenceAsync(Clip(extension), frameIndex);
    }

    [Fact(DisplayName = "ExtractFrameAsync: ненулевое стартовое время файла (10 с) — тот же кадр N по тому же моменту SeekTimeFor")]
    public async Task Non_zero_start_time_yields_same_frame()
    {
        await AssertFrameMatchesReferenceAsync(Offset, 37);
        await AssertFrameMatchesReferenceAsync(Offset, 0);
    }

    [Fact(DisplayName = "ExtractFrameAsync: повёрнутый клип — кадр отдаётся ПОСЛЕ автоповорота (240×320) и равен эталону с тем же автоповоротом")]
    public async Task Rotated_clip_frame_is_rotated()
    {
        var png = await AssertFrameMatchesReferenceAsync(Rotated, 37);
        ReadPngSize(png).ShouldBe((ClipHeight, ClipWidth));
    }

    [Fact(DisplayName = "ExtractFrameAsync: JPEG с пределом 640 — 1920×1080 → 640×360 (пропорции), сигнатура SOI/EOI; кадр меньше предела не увеличивается; повёрнутый — предел по высоте")]
    public async Task Jpeg_and_max_side()
    {
        var extractor = Extractor();

        var jpeg = (await extractor.ExtractFrameAsync(Big, VideoProbe.SeekTimeFor(5, ClipFps), FrameImageFormat.Jpeg, maxSide: 640)).ShouldNotBeNull();
        jpeg.Length.ShouldBeGreaterThan(1024);
        jpeg[0].ShouldBe((byte)0xFF);
        jpeg[1].ShouldBe((byte)0xD8);
        jpeg[^2].ShouldBe((byte)0xFF);
        jpeg[^1].ShouldBe((byte)0xD9);
        DecodedSize(jpeg).ShouldBe((640, 360));

        // Без предела — исходный размер (снимок); предел больше кадра — кадр не увеличивается.
        var full = (await extractor.ExtractFrameAsync(Big, TimeSpan.Zero, FrameImageFormat.Png)).ShouldNotBeNull();
        ReadPngSize(full).ShouldBe((1920, 1080));
        var untouched = (await extractor.ExtractFrameAsync(Clip("mp4"), TimeSpan.Zero, FrameImageFormat.Jpeg, maxSide: 640)).ShouldNotBeNull();
        DecodedSize(untouched).ShouldBe((ClipWidth, ClipHeight));

        // Повёрнутый 240×320 с пределом 200: ограничивается НАИБОЛЬШАЯ сторона (высота), а не ширина.
        var rotated = (await extractor.ExtractFrameAsync(Rotated, TimeSpan.Zero, FrameImageFormat.Png, maxSide: 200)).ShouldNotBeNull();
        ReadPngSize(rotated).ShouldBe((150, 200));
    }

    [Fact(DisplayName = "ExtractFrameAsync: момент за концом записи — null (не ошибка); последний кадр ещё есть")]
    public async Task Beyond_end_returns_null()
    {
        var extractor = Extractor();
        var probe = (await extractor.ProbeAsync(Clip("mp4"))).ShouldNotBeNull();
        var lastFrame = (long)Math.Round(probe.Duration!.Value.TotalSeconds * probe.FrameRate) - 1;

        (await extractor.ExtractFrameAsync(Clip("mp4"), VideoProbe.SeekTimeFor(lastFrame, probe.FrameRate), FrameImageFormat.Png)).ShouldNotBeNull();
        (await extractor.ExtractFrameAsync(Clip("mp4"), probe.Duration!.Value, FrameImageFormat.Png)).ShouldBeNull();
        (await extractor.ExtractFrameAsync(Clip("mp4"), TimeSpan.FromSeconds(10), FrameImageFormat.Jpeg, maxSide: 640)).ShouldBeNull();
        (await extractor.ExtractFrameAsync(Clip("mkv"), TimeSpan.FromSeconds(10), FrameImageFormat.Png)).ShouldBeNull();
    }

    [Fact(DisplayName = "ExtractFrameAsync: файл без видеопотока — InvalidOperationException; отмена — OperationCanceledException; таймаут — InvalidOperationException с причиной")]
    public async Task Failures_are_explicit()
    {
        var extractor = Extractor();

        var error = await Should.ThrowAsync<InvalidOperationException>(
            () => extractor.ExtractFrameAsync(AudioOnly, TimeSpan.Zero, FrameImageFormat.Png));
        error.Message.ShouldStartWith("ffmpeg не вырезал кадр");

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(
            () => extractor.ExtractFrameAsync(Clip("mp4"), TimeSpan.Zero, FrameImageFormat.Png, cancellationToken: cancelled.Token));

        // Таймаут — ошибка с причиной, а не отмена: для вызывающего это сбой вырезки, не отмена его задачи.
        var impatient = new FfmpegFrameExtractor(Options()) { FrameExtractionTimeoutOverride = TimeSpan.FromMilliseconds(1) };
        var timeout = await Should.ThrowAsync<InvalidOperationException>(
            () => impatient.ExtractFrameAsync(Big, TimeSpan.FromSeconds(0.5), FrameImageFormat.Png));
        timeout.Message.ShouldContain("не вырезал кадр за 0.001 с");
    }

    /// <summary>
    /// Вырезает PNG кадра № <paramref name="frameIndex"/> по моменту <see cref="VideoProbe.SeekTimeFor"/> и сверяет его
    /// пиксели с эталоном — кадром N того же файла, выбранным по НОМЕРУ (<c>select=eq(n,N)</c>) отдельным прогоном
    /// ffmpeg в rawvideo RGB24. Возвращает PNG для дополнительных проверок.
    /// </summary>
    private async Task<byte[]> AssertFrameMatchesReferenceAsync(string path, int frameIndex)
    {
        var extractor = Extractor();
        var probe = (await extractor.ProbeAsync(path)).ShouldNotBeNull();

        var png = (await extractor.ExtractFrameAsync(path, VideoProbe.SeekTimeFor(frameIndex, probe.FrameRate), FrameImageFormat.Png))
            .ShouldNotBeNull($"кадр {frameIndex} из {Path.GetFileName(path)}");
        png.AsSpan(0, PngSignature.Length).SequenceEqual(PngSignature).ShouldBeTrue("сигнатура PNG");
        ReadPngSize(png).ShouldBe((probe.Width, probe.Height), "размер кадра — как в пробе");

        var reference = Path.Combine(_workDir, $"ref-{Path.GetFileName(path)}-{frameIndex}.raw");
        await RunAsync($"-y -i \"{path}\" -vf select=eq(n\\,{frameIndex.ToString(CultureInfo.InvariantCulture)}) -frames:v 1 -f rawvideo -pix_fmt rgb24 \"{reference}\"");
        var expected = await File.ReadAllBytesAsync(reference);
        expected.Length.ShouldBe(probe.Width * probe.Height * 3, "эталон — RGB24 того же размера");

        DecodeRgb(png).ShouldBe(expected, $"пиксели кадра {frameIndex} из {Path.GetFileName(path)} (частота {probe.FrameRate})");
        return png;
    }

    private FfmpegFrameExtractor Extractor() => new(Options());

    private VisionOptions Options() => new("d.onnx", "00", "e.onnx", "00", FfmpegFolder: _ffmpegFolder);

    private Task RunAsync(string arguments) => VideoTestEnvironment.RunFfmpegAsync(_ffmpegFolder, arguments);

    private string Clip(string extension) => Path.Combine(_workDir, "clip." + extension);

    private string Live(string extension) => Path.Combine(_workDir, "live." + extension);

    private string Rotated => Path.Combine(_workDir, "rotated.mp4");

    private string Offset => Path.Combine(_workDir, "offset.mkv");

    private string Big => Path.Combine(_workDir, "big.mp4");

    private string AudioOnly => Path.Combine(_workDir, "audio.m4a");

    // Размер из заголовка IHDR (байты 16..23, big-endian) — без декодирования.
    private static (int Width, int Height) ReadPngSize(byte[] png) =>
        (BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16, 4)), BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20, 4)));

    private static (int Width, int Height) DecodedSize(byte[] image)
    {
        using var bitmap = SKBitmap.Decode(image).ShouldNotBeNull("изображение декодируется");
        return (bitmap.Width, bitmap.Height);
    }

    // Пиксели PNG как RGB24 построчно (SkiaSharp декодирует без потерь): сравнимо с rawvideo -pix_fmt rgb24.
    private static byte[] DecodeRgb(byte[] png)
    {
        var (width, height) = ReadPngSize(png);
        using var bitmap = SKBitmap.Decode(png, new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque))
            .ShouldNotBeNull("PNG декодируется");
        var pixels = bitmap.GetPixelSpan();
        var rgb = new byte[width * height * 3];
        for (var y = 0; y < height; y++)
        {
            var row = pixels.Slice(y * bitmap.RowBytes, width * 4);
            for (var x = 0; x < width; x++)
            {
                rgb[(y * width + x) * 3] = row[x * 4];
                rgb[(y * width + x) * 3 + 1] = row[x * 4 + 1];
                rgb[(y * width + x) * 3 + 2] = row[x * 4 + 2];
            }
        }

        return rgb;
    }
}
