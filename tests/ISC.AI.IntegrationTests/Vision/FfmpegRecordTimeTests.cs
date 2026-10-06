using System;
using System.IO;
using System.Threading.Tasks;
using ISC.AI.Vision.Onnx;
using ISC.AI.Vision.Onnx.Video;
using Shouldly;
using Xunit;

namespace ISC.AI.IntegrationTests.Vision;

/// <summary>
/// Встроенное время начала записи (ТФ-МЕД-11, ADR-0038) НАСТОЯЩИМ ffprobe: <c>creation_time</c>, записанный в mp4, mov,
/// mkv и webm, попадает в пробу видео как есть (UTC); файл без этого тега — без времени, а не «1970 год».
/// </summary>
/// <remarks>Поставка ffmpeg — см. <see cref="VideoTestEnvironment"/>; без неё тесты падают с инструкцией.</remarks>
[Trait("Category", "Video")]
public sealed class FfmpegRecordTimeTests : IAsyncLifetime
{
    private const string CreationTime = "2022-12-08T08:08:12.000000Z";

    private string _ffmpegFolder = string.Empty;
    private string _workDir = string.Empty;

    /// <summary>Клипы по секунде: с тегом времени создания в четырёх контейнерах и mp4 без него.</summary>
    public async Task InitializeAsync()
    {
        _ffmpegFolder = VideoTestEnvironment.LocateFfmpegFolder();
        _workDir = VideoTestEnvironment.CreateWorkDir();

        const string source = "-f lavfi -i testsrc=size=320x240:rate=25 -t 1";
        var tag = $"-metadata creation_time={CreationTime}";
        await RunAsync($"-y {source} -c:v h264_mf {tag} \"{Clip("tagged.mp4")}\"");
        await RunAsync($"-y {source} -c:v h264_mf {tag} \"{Clip("tagged.mov")}\"");
        await RunAsync($"-y {source} -c:v h264_mf {tag} \"{Clip("tagged.mkv")}\"");
        await RunAsync($"-y {source} -c:v libvpx-vp9 -b:v 100k {tag} \"{Clip("tagged.webm")}\"");
        await RunAsync($"-y {source} -c:v h264_mf \"{Clip("plain.mp4")}\"");
    }

    /// <inheritdoc />
    public Task DisposeAsync()
    {
        VideoTestEnvironment.TryDeleteWorkDir(_workDir);
        return Task.CompletedTask;
    }

    [Theory(DisplayName = "ProbeAsync: creation_time контейнера — время начала записи (UTC) в mp4, mov, mkv и webm")]
    [InlineData("tagged.mp4")]
    [InlineData("tagged.mov")]
    [InlineData("tagged.mkv")]
    [InlineData("tagged.webm")]
    public async Task Probe_reads_creation_time(string fileName)
    {
        var probe = (await Extractor().ProbeAsync(Clip(fileName))).ShouldNotBeNull();

        probe.RecordedAt.ShouldBe(new DateTimeOffset(2022, 12, 8, 8, 8, 12, TimeSpan.Zero));
    }

    [Fact(DisplayName = "ProbeAsync: без тега времени создания — RecordedAt null, остальная проба как обычно")]
    public async Task Probe_without_tag_has_no_recorded_time()
    {
        var probe = (await Extractor().ProbeAsync(Clip("plain.mp4"))).ShouldNotBeNull();

        probe.RecordedAt.ShouldBeNull();
        probe.FrameRate.ShouldBe(25);
    }

    private FfmpegFrameExtractor Extractor() => new(new VisionOptions("d.onnx", "00", "e.onnx", "00", FfmpegFolder: _ffmpegFolder));

    private Task RunAsync(string arguments) => VideoTestEnvironment.RunFfmpegAsync(_ffmpegFolder, arguments);

    private string Clip(string fileName) => Path.Combine(_workDir, fileName);
}
