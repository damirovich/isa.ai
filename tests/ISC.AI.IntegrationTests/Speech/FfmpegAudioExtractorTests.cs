using System;
using System.IO;
using System.Threading.Tasks;
using ISC.AI.Speech.Audio;
using Shouldly;
using Xunit;

namespace ISC.AI.IntegrationTests.Speech;

/// <summary>
/// Извлечение звука настоящим ffmpeg поставки (ADR-0026) — ОТДЕЛЬНО от распознавания: «ноль фрагментов» на
/// выходе адаптера одинаково дают и извлечённая тишина, и пропущенная дорожка, поэтому здесь проверяется сам WAV
/// (16 кГц, моно, 16 бит, длина — как у исходника) и предел длительности. Исходники генерирует ffmpeg (тон,
/// тишина, «немое» видео) — записей людей нет (ТО-прог-13). Модели и процесс-распознаватель не нужны.
/// </summary>
[Trait("Category", "Speech")]
[Collection(SpeechTestEnvironment.CollectionName)]
public sealed class FfmpegAudioExtractorTests : IAsyncLifetime
{
    private const int ClipSeconds = 5;
    private const int ToleranceMs = 50;

    private static readonly TimeSpan Day = TimeSpan.FromHours(24);

    private readonly string _workDir = Path.Combine(Path.GetTempPath(), "iscai-speech-extractor-tests", Guid.NewGuid().ToString("N"));

    private string _ffmpegFolder = string.Empty;

    /// <inheritdoc />
    public Task InitializeAsync()
    {
        _ffmpegFolder = SpeechTestEnvironment.LocateFfmpegFolder();
        Directory.CreateDirectory(_workDir);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task DisposeAsync()
    {
        try
        {
            Directory.Delete(_workDir, recursive: true);
        }
        catch (IOException)
        {
            // Временный каталог — не повод валить прогон.
        }

        return Task.CompletedTask;
    }

    [Fact(DisplayName = "ADR-0026: звуковая дорожка видео (MPEG-4 + AAC) извлекается в WAV 16 кГц моно 16 бит длиной как у ролика")]
    public async Task Video_sound_track_is_extracted()
    {
        var mp4 = Path.Combine(_workDir, "clip.mp4");
        await SpeechTestEnvironment.RunFfmpegAsync(_ffmpegFolder,
            $"-y -f lavfi -i testsrc2=size=320x240:rate=25 -f lavfi -i anullsrc=r=48000:cl=stereo -t {ClipSeconds} "
            + $"-c:v mpeg4 -q:v 5 -c:a aac -shortest \"{mp4}\"");

        await ShouldExtractModelWaveAsync(mp4);
    }

    [Fact(DisplayName = "ADR-0026: сжатое аудио (m4a/AAC 44,1 кГц, стерео) извлекается в WAV 16 кГц моно 16 бит длиной как у записи")]
    public async Task Compressed_audio_is_extracted()
    {
        var m4a = Path.Combine(_workDir, "tone.m4a");
        await SpeechTestEnvironment.RunFfmpegAsync(_ffmpegFolder,
            "-y -f lavfi -i sine=frequency=440:sample_rate=44100:duration=3 -f lavfi -i anullsrc=r=44100:cl=stereo "
            + $"-filter_complex \"[0:a]aformat=channel_layouts=stereo[t];[t][1:a]concat=n=2:v=0:a=1[out]\" -map \"[out]\" -t {ClipSeconds} -c:a aac \"{m4a}\"");

        await ShouldExtractModelWaveAsync(m4a);
    }

    [Fact(DisplayName = "ADR-0026: видео без звуковой дорожки — «звука нет» (null), WAV не создаётся, это не ошибка")]
    public async Task Video_without_audio_gives_no_audio()
    {
        var mp4 = Path.Combine(_workDir, "mute.mp4");
        await SpeechTestEnvironment.RunFfmpegAsync(_ffmpegFolder,
            $"-y -f lavfi -i testsrc2=size=320x240:rate=25 -t {ClipSeconds} -c:v mpeg4 -q:v 5 \"{mp4}\"");
        var wav = Path.Combine(_workDir, "mute.wav");

        var audio = await new FfmpegAudioExtractor(_ffmpegFolder, Day).ExtractAsync(mp4, wav);

        audio.ShouldBeNull();
        File.Exists(wav).ShouldBeFalse();
    }

    [Fact(DisplayName = "ADR-0026: запись длиннее предела по данным ffprobe — отказ «разбейте на части» ДО запуска ffmpeg (WAV не создаётся)")]
    public async Task Record_longer_than_limit_is_refused_before_ffmpeg()
    {
        var m4a = Path.Combine(_workDir, "long.m4a");
        await SpeechTestEnvironment.RunFfmpegAsync(_ffmpegFolder,
            $"-y -f lavfi -i anullsrc=r=16000:cl=mono -t {ClipSeconds} -c:a aac \"{m4a}\"");
        var wav = Path.Combine(_workDir, "long.wav");

        var error = await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            await new FfmpegAudioExtractor(_ffmpegFolder, TimeSpan.FromSeconds(2)).ExtractAsync(m4a, wav);
        });

        error.Message.ShouldContain("по данным ffprobe — 5 с");
        error.Message.ShouldContain("Speech:MaxDurationHours");
        File.Exists(wav).ShouldBeFalse("ffmpeg не запускался");
    }

    [Fact(DisplayName = "ADR-0026: длительность в заголовке не указана (Matroska «живого потока») — потолок -t и сверка длины WAV всё равно отказывают, хвост не обрезается молча")]
    public async Task Record_without_duration_is_capped_and_refused()
    {
        // Matroska в режиме живого потока (-live 1) не знает своей длительности: ffprobe не сообщает её ни для
        // контейнера, ни для дорожки — предварительная проверка пропускает, и предел держит только потолок ffmpeg.
        var mka = Path.Combine(_workDir, "stream.mka");
        await SpeechTestEnvironment.RunFfmpegAsync(_ffmpegFolder,
            $"-y -f lavfi -i sine=frequency=440:sample_rate=48000:duration={ClipSeconds} -c:a libopus -f matroska -live 1 \"{mka}\"");
        var wav = Path.Combine(_workDir, "stream.wav");

        var error = await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            await new FfmpegAudioExtractor(_ffmpegFolder, TimeSpan.FromSeconds(2)).ExtractAsync(mka, wav);
        });

        error.Message.ShouldContain("длительность не указал");
        error.Message.ShouldContain("Speech:MaxDurationHours");
        SpeechTestEnvironment.ReadWave(wav).Duration.ShouldBe(TimeSpan.FromSeconds(3), "потолок -t — предел + 1 с");

        // Тот же файл в пределах — извлекается целиком.
        (await new FfmpegAudioExtractor(_ffmpegFolder, Day).ExtractAsync(mka, wav)).ShouldNotBeNull()
            .Duration.TotalMilliseconds.ShouldBeInRange(ClipSeconds * 1000 - ToleranceMs, ClipSeconds * 1000 + ToleranceMs);
    }

    private async Task ShouldExtractModelWaveAsync(string source)
    {
        var wav = Path.Combine(_workDir, Path.GetFileNameWithoutExtension(source) + ".wav");

        var audio = await new FfmpegAudioExtractor(_ffmpegFolder, Day).ExtractAsync(source, wav);

        audio.ShouldNotBeNull("звуковая дорожка есть — «звука нет» здесь означало бы, что ffprobe её не увидел");
        var header = SpeechTestEnvironment.ReadWave(wav);
        header.SampleRate.ShouldBe(16000);
        header.Channels.ShouldBe(1);
        header.BitsPerSample.ShouldBe(16);
        header.Duration.TotalMilliseconds.ShouldBeInRange(ClipSeconds * 1000 - ToleranceMs, ClipSeconds * 1000 + ToleranceMs);
        audio.Duration.ShouldBe(header.Duration, "длительность для таймаута — ровно длина WAV");
    }
}
