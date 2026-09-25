using System;
using System.IO;
using System.Threading.Tasks;
using ISC.AI.Speech;
using ISC.AI.Speech.Audio;
using Shouldly;
using Xunit;

namespace ISC.AI.UnitTests.Speech;

/// <summary>
/// Пределы прогона расшифровки (ADR-0026) без ffmpeg и процесса-распознавателя: предел длительности записи
/// (граница — на подменённой длительности ffprobe), расчёт таймаута, отказ для путей, которые нельзя безопасно
/// передать в командной строке ffprobe/ffmpeg.
/// </summary>
public sealed class SpeechLimitsTests
{
    private static readonly TimeSpan Day = TimeSpan.FromHours(24);

    [Fact(DisplayName = "ADR-0026: запись ровно по пределу проходит, на тик длиннее — отказ «разбейте на части» с ключом Speech:MaxDurationHours")]
    public void Duration_limit_boundary()
    {
        FfmpegAudioExtractor.EnsureWithinLimit(Day, Day);
        FfmpegAudioExtractor.EnsureWithinLimit(TimeSpan.FromMinutes(5), Day);

        var error = Should.Throw<InvalidOperationException>(() => FfmpegAudioExtractor.EnsureWithinLimit(Day + TimeSpan.FromTicks(1), Day));

        error.Message.ShouldStartWith("Запись длиннее 24 ч");
        error.Message.ShouldContain("разбейте запись на части не длиннее 24 ч");
        error.Message.ShouldContain("Speech:MaxDurationHours");
    }

    [Fact(DisplayName = "ADR-0026: 45-часовая запись (переполнение int32 детектора речи) отвергается и при наибольшем пределе 36 ч; фактическая длительность — в тексте")]
    public void Record_beyond_detector_limit_is_refused()
    {
        var error = Should.Throw<InvalidOperationException>(() =>
            FfmpegAudioExtractor.EnsureWithinLimit(TimeSpan.FromHours(45) + TimeSpan.FromMinutes(12), TimeSpan.FromHours(36)));

        error.Message.ShouldContain("длиннее 36 ч");
        error.Message.ShouldContain("45 ч 12 мин");
    }

    [Fact(DisplayName = "Длительность неизвестна (ffprobe не сообщил) — предварительная проверка пропускает: предел держит потолок -t и сверка длины WAV")]
    public void Unknown_duration_passes_precheck() =>
        FfmpegAudioExtractor.EnsureWithinLimit(null, Day);

    [Fact(DisplayName = "Длительность для предела — наибольшая из контейнера и звуковых дорожек (худший случай); нули — «неизвестна»")]
    public void Probed_duration_takes_the_longest()
    {
        FfmpegAudioExtractor.ProbedDuration(TimeSpan.FromMinutes(3), [TimeSpan.FromHours(30), TimeSpan.FromMinutes(2)])
            .ShouldBe(TimeSpan.FromHours(30));
        FfmpegAudioExtractor.ProbedDuration(TimeSpan.FromHours(2), [TimeSpan.FromMinutes(1)])
            .ShouldBe(TimeSpan.FromHours(2));
        FfmpegAudioExtractor.ProbedDuration(TimeSpan.Zero, [TimeSpan.Zero]).ShouldBeNull();
        FfmpegAudioExtractor.ProbedDuration(TimeSpan.Zero, []).ShouldBeNull();
    }

    [Theory(DisplayName = "Таймаут прогона: 15 мин + множитель × длительность WAV; длительность неизвестна или больше предела — от предела")]
    [InlineData(60.0, 3.0, 24.0, 15.0 + 180)]
    [InlineData(0.5, 3.0, 24.0, 15.0 + 1.5)]
    [InlineData(60.0, 10.0, 24.0, 15.0 + 600)]
    [InlineData(null, 3.0, 24.0, 15.0 + 3 * 24 * 60)]
    [InlineData(0.0, 3.0, 24.0, 15.0 + 3 * 24 * 60)]
    [InlineData(48 * 60.0, 3.0, 24.0, 15.0 + 3 * 24 * 60)]
    [InlineData(null, 2.0, 1.0, 15.0 + 2 * 60)]
    public void Run_timeout_is_computed(double? durationMinutes, double factor, double limitHours, double expectedMinutes)
    {
        var options = new SpeechOptions("w.exe", "m", "00", "t", "00", "v", "00", MaxDurationHours: limitHours, TimeoutFactor: factor);
        var duration = durationMinutes is { } minutes ? TimeSpan.FromMinutes(minutes) : (TimeSpan?)null;

        SpeechWorkerTranscriber.ComputeRunTimeout(duration, options).ShouldBe(TimeSpan.FromMinutes(expectedMinutes));
    }

    [Fact(DisplayName = "Наибольший таймаут (36 ч × 20 + 15 мин) укладывается в таймер CancelAfter — «вечного» прогона нет")]
    public void Largest_timeout_fits_timer()
    {
        var options = new SpeechOptions("w.exe", "m", "00", "t", "00", "v", "00", MaxDurationHours: 1000, TimeoutFactor: 1000);

        var timeout = SpeechWorkerTranscriber.ComputeRunTimeout(null, options);

        timeout.ShouldBe(TimeSpan.FromMinutes(15) + TimeSpan.FromHours(36 * 20));
        timeout.TotalMilliseconds.ShouldBeLessThan(uint.MaxValue - 1);
    }

    [Theory(DisplayName = "Командная строка ffmpeg: кавычка в пути или обратная косая черта в конце — отказ до запуска процессов; сам путь в текст не попадает")]
    [InlineData("/tmp/isc-speech-1.m4a\" -y \"out")]
    [InlineData("/tmp/isc-speech-1.m4a\\")]
    [InlineData("C:\\temp\\запись\".ogg")]
    public void Unsafe_command_line_paths_are_refused(string path)
    {
        var error = Should.Throw<InvalidOperationException>(() => FfmpegAudioExtractor.EnsureSafeForCommandLine(path));

        error.Message.ShouldContain("кавычку");
        error.Message.ShouldNotContain("isc-speech-1");
        error.Message.ShouldNotContain("запись");
    }

    [Theory(DisplayName = "Обычные пути (пробелы, кириллица, обратные косые черты внутри) командной строке не мешают")]
    [InlineData("C:\\Users\\Служба\\AppData\\Local\\Temp\\isc-speech-1.m4a")]
    [InlineData("/tmp/isc speech/запись 1.ogg")]
    public void Ordinary_paths_are_accepted(string path) =>
        FfmpegAudioExtractor.EnsureSafeForCommandLine(path);

    [Fact(DisplayName = "Извлекатель отвергает небезопасный путь ДО ffprobe: отказ — о пути, а не «ffprobe не разобрал файл»")]
    public async Task Extractor_refuses_unsafe_path_before_ffprobe()
    {
        var extractor = new FfmpegAudioExtractor(Path.Combine(Path.GetTempPath(), "нет-ffmpeg-" + Guid.NewGuid().ToString("N")), Day);

        var error = await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            await extractor.ExtractAsync("запись.m4a\\", Path.Combine(Path.GetTempPath(), "out.wav"));
        });

        error.Message.ShouldContain("кавычку");
        error.Message.ShouldNotContain("ffprobe не разобрал");
    }

    [Fact(DisplayName = "Извлекатель: предел не больше жёсткого (36 ч), нулевой или отрицательный — ошибка программиста")]
    public void Extractor_limit_is_validated()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => new FfmpegAudioExtractor(null, TimeSpan.Zero));
        Should.NotThrow(() => new FfmpegAudioExtractor(null, TimeSpan.FromHours(100)));
    }
}
