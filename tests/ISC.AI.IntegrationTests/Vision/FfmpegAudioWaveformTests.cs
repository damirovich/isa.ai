using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ISC.AI.Modules.Media.Domain.Model;
using ISC.AI.Vision.Onnx;
using ISC.AI.Vision.Onnx.Video;
using Shouldly;
using Xunit;

namespace ISC.AI.IntegrationTests.Vision;

/// <summary>
/// Волна аудиозаписи НАСТОЯЩИМ ffmpeg (ADR-0047): запись «тон 1 с + тишина 1 с» — пики в первой половине заметны, во
/// второй — нулевые (WAV) или около нуля (сжатый AAC); шаг 20 мс, около 100 пиков; длинная запись укрупняет окна до
/// предела; видео без звука, отсутствующий файл, отмена и таймаут — понятные ошибки. Синтетика из <c>lavfi</c> —
/// реальные голоса в тестах не используются.
/// </summary>
/// <remarks>Поставка ffmpeg — см. <see cref="VideoTestEnvironment"/>; без неё тесты падают с инструкцией.</remarks>
[Trait("Category", "Video")]
public sealed class FfmpegAudioWaveformTests : IAsyncLifetime
{
    private string _ffmpegFolder = string.Empty;
    private string _workDir = string.Empty;

    /// <summary>Тон 440 Гц 1 с и тишина 1 с — в WAV и в M4A (AAC); 5 минут тишины; видео без звука.</summary>
    public async Task InitializeAsync()
    {
        _ffmpegFolder = VideoTestEnvironment.LocateFfmpegFolder();
        _workDir = VideoTestEnvironment.CreateWorkDir();

        const string toneThenSilence = "-f lavfi -i sine=frequency=440:duration=1 -af apad=pad_dur=1";
        await RunAsync($"-y {toneThenSilence} -c:a pcm_s16le \"{PathOf("tone.wav")}\"");
        await RunAsync($"-y {toneThenSilence} -c:a aac -b:a 64k \"{PathOf("tone.m4a")}\"");
        await RunAsync($"-y -f lavfi -i anullsrc=r=8000:cl=mono -t 300 -c:a pcm_s16le \"{PathOf("long.wav")}\"");
        await RunAsync($"-y -f lavfi -i testsrc=size=160x120:rate=10 -t 1 -c:v mpeg4 \"{PathOf("silent.avi")}\"");
    }

    /// <inheritdoc />
    public Task DisposeAsync()
    {
        VideoTestEnvironment.TryDeleteWorkDir(_workDir);
        return Task.CompletedTask;
    }

    [Theory(DisplayName = "Тон 1 с + тишина 1 с: шаг 20 мс, около 100 пиков; тон заметен, тишина — ноль (WAV) или около нуля (AAC)")]
    [InlineData("tone.wav", 0)]
    [InlineData("tone.m4a", 3)]
    public async Task Tone_then_silence_shows_loud_half_and_quiet_half(string name, int silenceTolerance)
    {
        using var extractor = Extractor();
        var waveform = await extractor.ExtractAsync(PathOf(name));

        waveform.Version.ShouldBe(AudioWaveform.CurrentVersion);
        waveform.StepMs.ShouldBe(AudioWaveform.BaseWindowMs);
        waveform.Peaks.Length.ShouldBeInRange(98, 106);

        // Уровень тона lavfi sine — 1/8 полной шкалы → ≈ 32 из 255; края (нарастание кодека) не проверяем.
        waveform.Peaks.Skip(5).Take(40).ShouldAllBe(p => p >= 20 && p <= 45);
        waveform.Peaks.Skip(60).Take(35).ShouldAllBe(p => p <= silenceTolerance);
    }

    [Fact(DisplayName = "5 минут записи: окна укрупняются — 3000 пиков с шагом 100 мс (5 окон по 20 мс), покрыта вся запись")]
    public async Task Long_recording_is_reduced_to_max_peaks()
    {
        using var extractor = Extractor();
        var waveform = await extractor.ExtractAsync(PathOf("long.wav"));

        waveform.Peaks.Length.ShouldBe(AudioWaveform.MaxPeaks);
        waveform.StepMs.ShouldBe(100); // ⌈15 000 окон / 3000⌉ = 5 окон по 20 мс
        waveform.DurationMs.ShouldBe(300_000);
        waveform.Peaks.ShouldAllBe(p => p == 0);
    }

    [Fact(DisplayName = "Видео без звука — ошибка с причиной; нет файла — FileNotFoundException; отмена — отмена; таймаут — ошибка с причиной")]
    public async Task Failures_are_explicit()
    {
        using var extractor = Extractor();

        var noAudio = await Should.ThrowAsync<InvalidOperationException>(() => extractor.ExtractAsync(PathOf("silent.avi")));
        noAudio.Message.ShouldStartWith("ffmpeg не построил волну записи");

        await Should.ThrowAsync<FileNotFoundException>(() => extractor.ExtractAsync(PathOf("missing.wav")));

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(() => extractor.ExtractAsync(PathOf("tone.wav"), cancelled.Token));

        using var impatient = new FfmpegAudioWaveformExtractor(Options()) { TimeoutOverride = TimeSpan.FromMilliseconds(1) };
        var timeout = await Should.ThrowAsync<InvalidOperationException>(() => impatient.ExtractAsync(PathOf("long.wav")));
        timeout.Message.ShouldContain("не построил волну записи за 0.001 с");
    }

    private FfmpegAudioWaveformExtractor Extractor() => new(Options());

    private VisionOptions Options() => new("d.onnx", "00", "e.onnx", "00", FfmpegFolder: _ffmpegFolder);

    private Task RunAsync(string arguments) => VideoTestEnvironment.RunFfmpegAsync(_ffmpegFolder, arguments);

    private string PathOf(string name) => Path.Combine(_workDir, name);
}
