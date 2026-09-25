using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using ISC.AI.Speech;
using ISC.AI.Speech.Protocol;
using Shouldly;
using Xunit;

namespace ISC.AI.IntegrationTests.Speech;

/// <summary>
/// Процесс-распознаватель и адаптер БЕЗ настоящих моделей (ADR-0026): коды выхода, чистота stdout при отказе,
/// объяснение сбоя загрузки модели, цепочка адаптера «пины → ffmpeg → процесс → код выхода → уборка».
/// Нужны только сборка ISC.AI.Speech.Worker и поставка ffmpeg — файлы моделей подменены мусором.
/// </summary>
[Trait("Category", "Speech")]
[Collection(SpeechTestEnvironment.CollectionName)]
public sealed class SpeechWorkerProcessTests : IAsyncLifetime
{
    private readonly string _workDir = Path.Combine(Path.GetTempPath(), "iscai-speech-process-tests", Guid.NewGuid().ToString("N"));

    private string _worker = string.Empty;
    private string _model = string.Empty;
    private string _tokens = string.Empty;
    private string _vad = string.Empty;

    /// <inheritdoc />
    public Task InitializeAsync()
    {
        _worker = SpeechTestEnvironment.LocateWorker();
        Directory.CreateDirectory(_workDir);

        // «Модели» — не ONNX: нативный загрузчик обязан отказать, а утилита — объяснить это кодом 3.
        _model = Write("model.onnx", "это не модель"u8.ToArray());
        _tokens = Write("tokens.txt", "a 0\n<blk> 1\n"u8.ToArray());
        _vad = Write("silero_vad.onnx", "и это не модель"u8.ToArray());
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

    [Fact(DisplayName = "Код 2: нет входного файла — отказ до загрузки моделей; stdout пуст, в stderr полный путь")]
    public async Task Missing_input_exits_with_code_2()
    {
        var absent = Path.Combine(_workDir, "нет.wav");

        var run = await SpeechTestEnvironment.RunWorkerAsync(_worker, "--model", _model, "--tokens", _tokens, "--vad", _vad, "--input", absent);

        run.ExitCode.ShouldBe(SpeechWorkerExitCodes.InvalidArguments);
        run.StdoutLines.ShouldBeEmpty();
        run.Stderr.ShouldContain(absent);
    }

    [Fact(DisplayName = "Код 2: WAV не 16 кГц моно — отказ с описанием формата, модели не загружаются")]
    public async Task Wrong_wave_format_exits_with_code_2()
    {
        var wav = SpeechTestEnvironment.Pcm16MonoWave(1600);
        BitConverter.GetBytes(8000).CopyTo(wav, 24); // частота дискретизации в заголовке fmt
        var path = Write("8k.wav", wav);

        var run = await SpeechTestEnvironment.RunWorkerAsync(_worker, "--model", _model, "--tokens", _tokens, "--vad", _vad, "--input", path);

        run.ExitCode.ShouldBe(SpeechWorkerExitCodes.InvalidArguments);
        run.StdoutLines.ShouldBeEmpty();
        run.Stderr.ShouldContain("8000 Гц");
    }

    [Fact(DisplayName = "Код 3: повреждённые файлы моделей — «sherpa-onnx не загрузился», процесс не падает молча, stdout пуст")]
    public async Task Corrupt_models_exit_with_code_3()
    {
        var path = Write("silence.wav", SpeechTestEnvironment.Pcm16MonoWave(16000));

        var run = await SpeechTestEnvironment.RunWorkerAsync(_worker, "--model", _model, "--tokens", _tokens, "--vad", _vad, "--input", path);

        run.ExitCode.ShouldBe(SpeechWorkerExitCodes.ModelLoadFailed, run.Stderr);
        run.StdoutLines.ShouldBeEmpty();
        run.Stderr.ShouldContain("sherpa-onnx");
    }

    [Fact(DisplayName = "Адаптер целиком: пины верны, ffmpeg привёл звук, процесс отказал кодом 3 — понятная ошибка с диагностикой; временные файлы удалены")]
    public async Task Adapter_reports_model_load_failure_and_cleans_up()
    {
        var ffmpeg = SpeechTestEnvironment.LocateFfmpegFolder();
        var ogg = Path.Combine(_workDir, "voice.ogg");
        await SpeechTestEnvironment.RunFfmpegAsync(ffmpeg, $"-y -f lavfi -i anullsrc=r=48000:cl=mono -t 2 -c:a libopus \"{ogg}\"");

        var before = WorkDirs();
        using var transcriber = new SpeechWorkerTranscriber(new SpeechOptions(
            _worker, _model, Sha(_model), _tokens, Sha(_tokens), _vad, Sha(_vad), FfmpegFolder: ffmpeg, Threads: 1));

        var error = await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            await transcriber.TranscribeAsync(ogg);
        });

        error.Message.ShouldContain(SpeechWorkerExitCodes.Describe(SpeechWorkerExitCodes.ModelLoadFailed));
        error.Message.ShouldContain("Диагностика процесса");
        WorkDirs().Except(before).ShouldBeEmpty("временный WAV (копия материала) удалён и при сбое");
    }

    private string Write(string name, byte[] content)
    {
        var path = Path.Combine(_workDir, name);
        File.WriteAllBytes(path, content);
        return path;
    }

    private static string Sha(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private static string[] WorkDirs() =>
        Directory.Exists(SpeechWorkerTranscriber.WorkRoot) ? Directory.GetDirectories(SpeechWorkerTranscriber.WorkRoot) : [];
}
