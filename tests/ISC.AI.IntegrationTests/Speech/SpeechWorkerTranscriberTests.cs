using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ISC.AI.Modules.Media.Domain.Model;
using ISC.AI.Speech;
using ISC.AI.Speech.Protocol;
using Shouldly;
using Xunit;

namespace ISC.AI.IntegrationTests.Speech;

/// <summary>
/// Расшифровка НАСТОЯЩИМИ ffmpeg, процессом-распознавателем и моделями (ADR-0026): приведение звука из
/// аудио- и видеоконтейнеров, детектор речи, модель, протокол и уборка временных файлов. Исходные файлы
/// генерирует ffmpeg (тишина, тон, «немое» видео) — записей людей в репозитории и тестах нет (ТО-прог-13);
/// качество распознавания речи проверяется пилотом на записях заказчика, не здесь.
/// </summary>
/// <remarks>
/// Требуются: процесс-распознаватель, собранный ОТДЕЛЬНО (<c>dotnet build src/integrations/ISC.AI.Speech.Worker</c>
/// или путь к сборке поставки в переменной <see cref="SpeechTestEnvironment.WorkerVariable"/>), поставка ffmpeg
/// (export-ffmpeg.ps1) и моделей (export-speech-models.ps1 → deploy/offline/models/speech). Без них тест падает с
/// инструкцией — намеренно, вместо тихого пропуска. Прогон без поставки: <c>dotnet test --filter "Category!=Speech"</c>.
/// Путь декодирования моделью (признаки, модель, словарь) без записей людей проверяется диагностическим режимом
/// утилиты <c>--decode-whole</c>; качество распознавания речи — пилотом.
/// </remarks>
[Trait("Category", "Speech")]
[Collection(SpeechTestEnvironment.CollectionName)]
public sealed class SpeechWorkerTranscriberTests : IAsyncLifetime
{
    private const int ClipSeconds = 5;

    private readonly string _workDir = Path.Combine(Path.GetTempPath(), "iscai-speech-tests", Guid.NewGuid().ToString("N"));

    private SpeechOptions _options = null!;
    private string _ffmpegFolder = string.Empty;

    /// <inheritdoc />
    public Task InitializeAsync()
    {
        _ffmpegFolder = SpeechTestEnvironment.LocateFfmpegFolder();
        _options = SpeechTestEnvironment.LoadDeliveredOptions(SpeechTestEnvironment.LocateWorker(), _ffmpegFolder);
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

    [Fact(DisplayName = "ADR-0026: тишина — процесс-распознаватель выдаёт ноль фрагментов и итог done с длительностью записи")]
    public async Task Silence_gives_zero_segments_and_done()
    {
        var wav = Path.Combine(_workDir, "silence.wav");
        await SpeechTestEnvironment.RunFfmpegAsync(_ffmpegFolder,
            $"-y -f lavfi -i anullsrc=r=16000:cl=mono -t {ClipSeconds} -c:a pcm_s16le \"{wav}\"");

        var run = await SpeechTestEnvironment.RunWorkerAsync(_options.WorkerPath,
            "--model", _options.ModelPath, "--tokens", _options.TokensPath, "--vad", _options.VadPath, "--input", wav, "--threads", "2");

        run.ExitCode.ShouldBe(SpeechWorkerExitCodes.Success, run.Stderr);
        run.StdoutLines.Count.ShouldBe(1, "в тишине — только итоговая строка");
        var done = SpeechWorkerProtocol.ParseLine(run.StdoutLines[0]).ShouldBeOfType<SpeechWorkerDone>();
        done.Segments.ShouldBe(0);
        done.DurationMs.ShouldBeInRange(ClipSeconds * 1000 - 50, ClipSeconds * 1000 + 50);
    }

    [Fact(DisplayName = "ADR-0026: путь декодирования моделью (диагностика --decode-whole) — синтетический шум без детектора речи доходит до модели кусками не длиннее предела: код 0, итог done")]
    public async Task Model_decode_path_runs_on_synthetic_sound()
    {
        // Тишину и тон детектор речи до модели не пускает, а записей людей в тестах нет (ТО-прог-13) — поэтому
        // признаки, модель и словарь проверяются в диагностическом режиме: весь WAV кусками прямо в модель.
        // Текст модели на шуме любой: пустой утилита в протокол не пишет, поэтому число обращений к модели
        // берётся из её итоговой строки в stderr («кусков декодировано N»), а строки фрагментов — если есть.
        const int noiseSeconds = 5;
        var wav = Path.Combine(_workDir, "noise.wav");
        await SpeechTestEnvironment.RunFfmpegAsync(_ffmpegFolder,
            $"-y -f lavfi -i anoisesrc=color=pink:amplitude=0.3:sample_rate=16000:duration={noiseSeconds} -ac 1 -c:a pcm_s16le \"{wav}\"");

        var run = await SpeechTestEnvironment.RunWorkerAsync(_options.WorkerPath,
            "--model", _options.ModelPath, "--tokens", _options.TokensPath, "--vad", _options.VadPath, "--input", wav,
            "--threads", "2", "--max-segment-seconds", "2", "--decode-whole");

        run.ExitCode.ShouldBe(SpeechWorkerExitCodes.Success, run.Stderr);
        var messages = run.StdoutLines.Select(SpeechWorkerProtocol.ParseLine).ToList();
        var segments = messages.OfType<SpeechWorkerSegment>().ToList();
        var done = messages[^1].ShouldBeOfType<SpeechWorkerDone>();

        var decoded = Regex.Match(run.Stderr, @"кусков декодировано (\d+)");
        decoded.Success.ShouldBeTrue(run.Stderr);
        int.Parse(decoded.Groups[1].Value, CultureInfo.InvariantCulture)
            .ShouldBeGreaterThanOrEqualTo(3, "5 с кусками не длиннее 2 с — не меньше трёх обращений к модели");
        done.Segments.ShouldBe(segments.Count);
        done.DurationMs.ShouldBeInRange(noiseSeconds * 1000 - 50, noiseSeconds * 1000 + 50);
        segments.ShouldAllBe(segment => segment.EndMs - segment.StartMs <= 2000 && segment.StartMs >= 0 && segment.EndMs <= done.DurationMs);
    }

    [Fact(DisplayName = "ADR-0026: тон и тишина в сжатом аудио (m4a/AAC) — адаптер целиком: тон не превращается в слова (ноль фрагментов), длительность — вся запись")]
    public async Task Tone_in_compressed_audio_gives_zero_segments()
    {
        // Непрерывный тон 440 Гц, затем тишина. Тон — не речь: фрагмент здесь означал бы, что гул или писк
        // в записи превращается в «сказанные» слова — это проверяется до пилота. Длительность из итога процесса
        // отличает «звук прочитан целиком» от «звука нет» (извлечение отдельно — FfmpegAudioExtractorTests).
        var m4a = Path.Combine(_workDir, "tone.m4a");
        await SpeechTestEnvironment.RunFfmpegAsync(_ffmpegFolder,
            "-y -f lavfi -i sine=frequency=440:sample_rate=44100:duration=3 -f lavfi -i anullsrc=r=44100:cl=stereo "
            + $"-filter_complex \"[0:a]aformat=channel_layouts=stereo[t];[t][1:a]concat=n=2:v=0:a=1[out]\" -map \"[out]\" -t {ClipSeconds} -c:a aac \"{m4a}\"");

        var transcription = await TranscribeAsync(m4a);

        transcription.Segments.ShouldBeEmpty();
        ShouldCoverClip(transcription);
    }

    [Fact(DisplayName = "ADR-0026: видео (MPEG-4 + AAC) проходит адаптер целиком: процесс прочитал звук длиной как у ролика; в тишине — ноль фрагментов")]
    public async Task Video_sound_track_passes_the_whole_adapter()
    {
        var mp4 = Path.Combine(_workDir, "clip.mp4");
        await SpeechTestEnvironment.RunFfmpegAsync(_ffmpegFolder,
            $"-y -f lavfi -i testsrc2=size=320x240:rate=25 -f lavfi -i anullsrc=r=48000:cl=stereo -t {ClipSeconds} "
            + $"-c:v mpeg4 -q:v 5 -c:a aac -shortest \"{mp4}\"");

        var transcription = await TranscribeAsync(mp4);

        transcription.Segments.ShouldBeEmpty();
        ShouldCoverClip(transcription);
    }

    [Fact(DisplayName = "ADR-0026: видео без звуковой дорожки (камеры наблюдения) — «звука нет»: ноль фрагментов без длительности, не ошибка")]
    public async Task Video_without_audio_gives_zero_segments()
    {
        var mp4 = Path.Combine(_workDir, "mute.mp4");
        await SpeechTestEnvironment.RunFfmpegAsync(_ffmpegFolder,
            $"-y -f lavfi -i testsrc2=size=320x240:rate=25 -t {ClipSeconds} -c:v mpeg4 -q:v 5 \"{mp4}\"");

        var transcription = await TranscribeAsync(mp4);

        transcription.Segments.ShouldBeEmpty();
        transcription.DurationMs.ShouldBeNull("процесс-распознаватель не запускался");
    }

    [Fact(DisplayName = "ADR-0026: прогон дольше таймаута — процесс остановлен, временный каталог удалён, наружу ошибка с ключом Speech:TimeoutFactor, а не отмена")]
    public async Task Timeout_stops_the_run_with_explicit_error()
    {
        var before = WorkDirs();
        var wav = Path.Combine(_workDir, "long.wav");
        await SpeechTestEnvironment.RunFfmpegAsync(_ffmpegFolder,
            "-y -f lavfi -i anullsrc=r=16000:cl=mono -t 600 -c:a pcm_s16le \"" + wav + "\"");

        using var transcriber = new SpeechWorkerTranscriber(_options) { TimeoutOverride = TimeSpan.FromMilliseconds(1500) };
        var error = await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            await transcriber.TranscribeAsync(wav);
        });

        error.Message.ShouldContain("по таймауту");
        error.Message.ShouldContain("Speech:TimeoutFactor");
        WorkDirs().Except(before).ShouldBeEmpty("временный каталог прогона удалён после таймаута");

        // После таймаута очередь свободна и следующий прогон проходит. Тестовый таймаут снимается: 1,5 с на
        // контрольный прогон под нагрузкой полного набора тестов не хватает даже на загрузку модели.
        transcriber.TimeoutOverride = null;
        (await transcriber.TranscribeAsync(await ShortSilenceAsync())).Segments.ShouldBeEmpty();
    }

    [Fact(DisplayName = "ТБ-064: временный WAV удаляется и при успехе, и при отмене; отмена прекращает расшифровку")]
    public async Task Temporary_files_are_removed_on_success_and_cancellation()
    {
        var before = WorkDirs();

        var wav = Path.Combine(_workDir, "long.wav");
        await SpeechTestEnvironment.RunFfmpegAsync(_ffmpegFolder,
            "-y -f lavfi -i anullsrc=r=16000:cl=mono -t 600 -c:a pcm_s16le \"" + wav + "\"");

        using var transcriber = new SpeechWorkerTranscriber(_options);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(1500));
        await Should.ThrowAsync<OperationCanceledException>(async () =>
        {
            await transcriber.TranscribeAsync(wav, cts.Token);
        });

        WorkDirs().Except(before).ShouldBeEmpty("временный каталог прогона удалён после отмены");

        // После отмены очередь свободна и следующий прогон проходит.
        (await TranscribeAsync(await ShortSilenceAsync())).Segments.ShouldBeEmpty();
        WorkDirs().Except(before).ShouldBeEmpty("временный каталог прогона удалён после успеха");
    }

    [Fact(DisplayName = "ТИ-004: неверный пин модели — расшифровка не запускается")]
    public async Task Wrong_pin_blocks_transcription()
    {
        using var transcriber = new SpeechWorkerTranscriber(_options with { ModelSha256 = new string('0', 64) });

        var error = await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            await transcriber.TranscribeAsync(await ShortSilenceAsync());
        });

        error.Message.ShouldContain("Целостность");
    }

    private async Task<AudioTranscription> TranscribeAsync(string path)
    {
        using var transcriber = new SpeechWorkerTranscriber(_options);
        transcriber.ModelVersion.ShouldStartWith(SpeechOptions.DefaultModelName + "@");

        return await transcriber.TranscribeAsync(path);
    }

    // Длительность из итога процесса (длина WAV, прочитанного распознавателем) — как у исходного ролика.
    private static void ShouldCoverClip(AudioTranscription transcription) =>
        transcription.DurationMs.ShouldNotBeNull("звук извлечён и прочитан процессом").ShouldBeInRange(ClipSeconds * 1000 - 50, ClipSeconds * 1000 + 50);

    private async Task<string> ShortSilenceAsync()
    {
        var wav = Path.Combine(_workDir, "short-" + Guid.NewGuid().ToString("N") + ".ogg");
        await SpeechTestEnvironment.RunFfmpegAsync(_ffmpegFolder,
            $"-y -f lavfi -i anullsrc=r=48000:cl=mono -t 2 -c:a libopus \"{wav}\"");
        return wav;
    }

    private static HashSet<string> WorkDirs() =>
        Directory.Exists(SpeechWorkerTranscriber.WorkRoot)
            ? Directory.GetDirectories(SpeechWorkerTranscriber.WorkRoot).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : [];
}
