using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ISC.AI.Modules.Media.Domain.Model;
using ISC.AI.Speech;
using ISC.AI.Speech.Protocol;
using ISC.AI.Speech.Speakers;
using Shouldly;
using Xunit;

namespace ISC.AI.IntegrationTests.Speech;

/// <summary>
/// Разделение говорящих НАСТОЯЩИМИ моделями, процессом-распознавателем и ffmpeg (ТФ-МЕД-19, ADR-0062 v0.3): модель
/// «голосовых отпечатков» загружается и проходит разметку, длинная запись пропускается с причиной, адаптер выбирает режим —
/// стерео с раздельными каналами по каналам, каналы-копии — моделью, «без дикторов» и без модели — без дикторов. Звук синтетический (тишина,
/// тон) — записей людей в тестах нет (ТО-прог-13); точность разделения голосов проверяется пилотом (КИ-10).
/// </summary>
/// <remarks>
/// Требуется то же, что тестам расшифровки (<see cref="SpeechWorkerTranscriberTests"/>), и модели говорящих
/// (<c>export-speech-models.ps1 -Speakers</c>). Без них тест падает с инструкцией.
/// </remarks>
[Trait("Category", "Speech")]
[Collection(SpeechTestEnvironment.CollectionName)]
public sealed class SpeakerDiarizationSpeechTests : IAsyncLifetime
{
    private const int Rate = 16_000;

    private readonly string _workDir = Path.Combine(Path.GetTempPath(), "iscai-speech-tests", Guid.NewGuid().ToString("N"));

    private SpeechOptions _options = null!;
    private SpeakerOptions _speakers = null!;

    /// <inheritdoc />
    public Task InitializeAsync()
    {
        _speakers = SpeechTestEnvironment.LoadDeliveredSpeakerOptions();
        _options = SpeechTestEnvironment.LoadDeliveredOptions(SpeechTestEnvironment.LocateWorker(), SpeechTestEnvironment.LocateFfmpegFolder())
            with { Speakers = _speakers };
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

    [Fact(DisplayName = "ADR-0062: модели говорящих загружаются и размечают запись — на тишине ноль реплик, исход done")]
    public async Task Speaker_models_load_and_run()
    {
        var wav = Path.Combine(_workDir, "silence.wav");
        await File.WriteAllBytesAsync(wav, SpeechTestEnvironment.Pcm16MonoWave(Rate * 5));

        var run = await RunWithSpeakersAsync(wav);

        run.ExitCode.ShouldBe(SpeechWorkerExitCodes.Success, run.Stderr);
        var messages = run.StdoutLines.Select(SpeechWorkerProtocol.ParseLine).ToList();
        messages.OfType<SpeechWorkerTurn>().ShouldBeEmpty();
        messages[^1].ShouldBeOfType<SpeechWorkerDone>().Diarization.ShouldBe(SpeechWorkerProtocol.DiarizationDone, run.Stderr);
        run.Stderr.ShouldContain("Дикторы размечены");
    }

    [Fact(DisplayName = "ADR-0062: запись длиннее предела — разметка пропущена с причиной, расшифровка не прерывается (код 0)")]
    public async Task Long_recording_is_skipped()
    {
        var wav = Path.Combine(_workDir, "long.wav");
        await File.WriteAllBytesAsync(wav, SpeechTestEnvironment.Pcm16MonoWave(Rate * 61));

        var run = await RunWithSpeakersAsync(wav, "--speaker-max-minutes", "1");

        run.ExitCode.ShouldBe(SpeechWorkerExitCodes.Success, run.Stderr);
        var done = SpeechWorkerProtocol.ParseLine(run.StdoutLines[^1]).ShouldBeOfType<SpeechWorkerDone>();
        (done.Diarization, done.DiarizationNote).ShouldBe(("skipped", "запись длиннее 1 мин"));
    }

    [Fact(DisplayName = "ADR-0062: адаптер — стерео с раздельными каналами размечается по каналам, без модели")]
    public async Task Stereo_with_separate_channels_is_marked_by_channels()
    {
        // Тон по очереди в левом и правом канале (как абоненты звонка, записанного раздельно).
        var wav = Path.Combine(_workDir, "stereo.wav");
        await File.WriteAllBytesAsync(wav, StereoTones((1.5, true, false), (1.5, false, true), (1.5, true, false), (1.5, false, true)));

        var result = await new SpeechWorkerTranscriber(_options).TranscribeAsync(wav);

        result.SpeakerSource.ShouldBe(TranscriptSpeakerSource.StereoChannels);
        result.SpeakerNote.ShouldBeNull();

        // v0.2: сотрудник задал троих — каналов два, значит разметку делает модель с этим числом; двое — по каналам.
        (await new SpeechWorkerTranscriber(_options).TranscribeAsync(wav, new SpeakerMarkupRequest(true, 3)))
            .SpeakerSource.ShouldBe(TranscriptSpeakerSource.Model);
        (await new SpeechWorkerTranscriber(_options).TranscribeAsync(wav, new SpeakerMarkupRequest(true, 2)))
            .SpeakerSource.ShouldBe(TranscriptSpeakerSource.StereoChannels);

        // v0.3: «без дикторов» — ни каналов, ни модели, без пометки.
        var off = await new SpeechWorkerTranscriber(_options).TranscribeAsync(wav, SpeakerMarkupRequest.Off);
        (off.SpeakerSource, off.SpeakerNote).ShouldBe((TranscriptSpeakerSource.None, (string?)null));
    }

    [Fact(DisplayName = "ADR-0062: адаптер — каналы-копии уходят модели; без моделей в настройке — без дикторов и без пометки")]
    public async Task Duplicate_channels_go_to_model_or_stay_unmarked()
    {
        var wav = Path.Combine(_workDir, "dup.wav");
        await File.WriteAllBytesAsync(wav, StereoTones((3, true, true)));

        var withModel = await new SpeechWorkerTranscriber(_options).TranscribeAsync(wav);
        withModel.SpeakerSource.ShouldBe(TranscriptSpeakerSource.Model);

        var withoutModel = await new SpeechWorkerTranscriber(_options with { Speakers = null }).TranscribeAsync(wav);
        withoutModel.SpeakerSource.ShouldBe(TranscriptSpeakerSource.None);
        withoutModel.SpeakerNote.ShouldBeNull();
    }

    private Task<SpeechTestEnvironment.WorkerRun> RunWithSpeakersAsync(string wav, params string[] extra) =>
        SpeechTestEnvironment.RunWorkerAsync(_options.WorkerPath,
        [
            "--model", _options.ModelPath, "--tokens", _options.TokensPath, "--vad", _options.VadPath, "--input", wav, "--threads", "2",
            "--speaker-embedding", _speakers.EmbeddingPath, .. extra,
        ]);

    // WAV 16 кГц, 2 канала: участки (секунды, звучит ли левый, звучит ли правый) — синус 440 Гц.
    private static byte[] StereoTones(params (double Seconds, bool Left, bool Right)[] parts)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        var frames = parts.Sum(p => (int)(p.Seconds * Rate));
        writer.Write("RIFF"u8);
        writer.Write(36 + frames * 4);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)2);
        writer.Write(Rate);
        writer.Write(Rate * 4);
        writer.Write((short)4);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(frames * 4);
        var t = 0;
        foreach (var (seconds, left, right) in parts)
        {
            for (var i = 0; i < (int)(seconds * Rate); i++, t++)
            {
                var sample = (short)(8000 * Math.Sin(2 * Math.PI * 440 * t / Rate));
                writer.Write(left ? sample : (short)0);
                writer.Write(right ? sample : (short)0);
            }
        }

        writer.Flush();
        return stream.ToArray();
    }
}
