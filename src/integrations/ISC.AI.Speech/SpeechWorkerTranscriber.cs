using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ISC.AI.Modules.Media.Domain.Model;
using ISC.AI.Modules.Media.Domain.Services;
using ISC.AI.Speech.Audio;
using ISC.AI.Speech.Protocol;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ISC.AI.Speech;

/// <summary>
/// Реализация порта <see cref="IAudioTranscriber"/> (ADR-0026): проверка пинов SHA-256 файлов модели →
/// ffmpeg приводит звук файла к WAV 16 кГц моно → ОТДЕЛЬНЫЙ процесс <c>ISC.AI.Speech.Worker</c> (Silero VAD +
/// GigaAM Multilingual через sherpa-onnx) → фрагменты и длительность записи из его stdout.
/// </summary>
/// <remarks>
/// <para>ПОЧЕМУ ОТДЕЛЬНЫЙ ПРОЦЕСС. Нативный onnxruntime sherpa-onnx и Microsoft.ML.OnnxRuntime распознавания
/// лиц лежат по одному пути <c>runtimes/win-x64/native/onnxruntime.dll</c> и это разные сборки — в одном
/// процессе одна затёрла бы другую. Заодно многочасовая расшифровка не делит память и потоки с хостом.</para>
/// <para>РЕЖИМ. Только локально (ТБ-052): модели читаются с диска после проверки пина (ТИ-004), сеть не
/// используется. Временный WAV — копия материала дела: он удаляется в любом исходе (успех, ошибка, отмена,
/// таймаут), а остатки прогона, который оборвала гибель хоста, убирает <see cref="SpeechWorkDirSweeper"/> при
/// следующем старте (ТБ-064). В тексты ошибок не попадает содержимое расшифровки (ТД-007). Отмена и таймаут
/// убивают дерево процессов.</para>
/// <para>Одновременно работает ОДИН процесс-распознаватель: модель занимает сотни мегабайт и все выделенные
/// потоки; параллельные прогоны на сервере с хостом и языковой моделью только замедлили бы всех.
/// Остальные вызовы ждут своей очереди (фоновые задачи).</para>
/// <para>ПРЕДЕЛЫ (ADR-0026). Фоновая очередь ядра одна на всю платформу: прогон, который идёт часами или
/// завис, останавливает индексацию лиц, документов и все прочие расшифровки. Поэтому запись длиннее
/// <see cref="SpeechOptions.MaxDuration"/> отвергается до запуска процессов (<see cref="FfmpegAudioExtractor"/>),
/// а весь прогон ограничен таймаутом <see cref="ComputeRunTimeout"/>: по его срабатыванию процессы
/// останавливаются и расшифровка — обычная ошибка с причиной (не отмена задачи).</para>
/// </remarks>
public sealed class SpeechWorkerTranscriber : IAudioTranscriber, IDisposable
{
    /// <summary>
    /// Постоянная часть таймаута прогона: запуск процессов, проверка пинов, загрузка модели, разбор файла
    /// ffprobe и приведение звука ffmpeg — не зависит от длины записи.
    /// </summary>
    public static readonly TimeSpan BaseRunTimeout = TimeSpan.FromMinutes(15);

    // Хвост stderr для текста ошибки: последние строки объясняют сбой, весь поток не нужен.
    private const int MaxStderrChars = 4000;
    private const int DeleteAttempts = 5;

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly TimeSpan ExitWaitTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan DeleteRetryDelay = TimeSpan.FromMilliseconds(200);

    /// <summary>Корень временных каталогов прогонов (<c>%TEMP%/iscai-speech</c>).</summary>
    public static readonly string WorkRoot = Path.Combine(Path.GetTempPath(), "iscai-speech");

    private readonly SpeechOptions _options;
    private readonly ILogger _logger;
    private readonly FfmpegAudioExtractor _extractor;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly DateTime _processStartUtc = SpeechWorkDirSweeper.ProcessStartUtc();

    // Свои каталоги, которые не удалось удалить сразу (файл ещё держал остановленный процесс). Доступ — только
    // под _gate: повтор перед следующим прогоном, до старта хоста ждать не нужно.
    private readonly List<string> _undeletedWorkDirs = [];

    /// <summary>Создаёт реализацию; диск и процессы на этом шаге не трогаются.</summary>
    /// <param name="options">Настройки секции <c>Speech</c>.</param>
    /// <param name="logger">Журнал приложения (необязателен: без него — молчаливый).</param>
    public SpeechWorkerTranscriber(SpeechOptions options, ILogger<SpeechWorkerTranscriber>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        _extractor = new FfmpegAudioExtractor(options.FfmpegFolder, options.MaxDuration);
    }

    /// <summary>
    /// Только для тестов: постоянный таймаут всего прогона вместо <see cref="ComputeRunTimeout"/> — проверка
    /// остановки по таймауту без многочасового ожидания.
    /// </summary>
    internal TimeSpan? TimeoutOverride { get; set; }

    /// <inheritdoc />
    public string ModelVersion => _options.ModelVersion;

    /// <inheritdoc />
    /// <exception cref="FileNotFoundException">Нет исходного файла, процесса-распознавателя или файла модели.</exception>
    /// <exception cref="InvalidOperationException">
    /// Пин не задан или не совпал; запись длиннее предела; ffmpeg не извлёк звук; процесс-распознаватель
    /// завершился с ошибкой или нарушил протокол; прогон не уложился в таймаут (текст объясняет причину).
    /// </exception>
    /// <exception cref="OperationCanceledException">Отмена вызывающим (не таймаут).</exception>
    public async Task<AudioTranscription> TranscribeAsync(string sourcePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException($"Файл для расшифровки не найден: {Path.GetFullPath(sourcePath)}", sourcePath);
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var workDir = Path.Combine(WorkRoot, Guid.NewGuid().ToString("N"));
        FileStream? runLock = null;

        // Таймаут — на ВЕСЬ прогон после очереди. Пока длительность неизвестна (до ffprobe/ffmpeg), он считается
        // от предела длительности; когда WAV готов — пересчитывается от его длины за вычетом уже прошедшего.
        var clock = Stopwatch.StartNew();
        TimeSpan? duration = null;
        var budget = RunBudget(duration);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(budget);
        try
        {
            // Пины проверяются при КАЖДОМ запуске, а не один раз при старте хоста: файл модели читает другой
            // процесс при каждом прогоне, и подмена между запусками не должна пройти незамеченной (ТИ-004).
            var files = VerifyFiles();

            SweepLeftovers();
            Directory.CreateDirectory(workDir);
            runLock = SpeechWorkDirSweeper.AcquireRunLock(workDir);
            var wavPath = Path.Combine(workDir, "audio.wav");
            var audio = await _extractor.ExtractAsync(sourcePath, wavPath, deadline.Token).ConfigureAwait(false);
            if (audio is null)
            {
                // Звуковой дорожки нет (видео камер наблюдения) — говорить в записи некому: ноль фрагментов.
                return AudioTranscription.NoAudio;
            }

            duration = audio.Duration;
            budget = RunBudget(duration);
            var remaining = budget - clock.Elapsed;
            deadline.CancelAfter(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);

            return await RunWorkerAsync(files, wavPath, deadline.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // Сработал таймаут, а не отмена вызывающим: процессы уже остановлены (регистрация на токене), каталог
            // удалит finally. Наружу — ошибка с причиной, а не OperationCanceledException: для конвейера это сбой
            // прогона, а не отмена задачи.
            throw new InvalidOperationException(TimeoutMessage(budget, duration), exception);
        }
        finally
        {
            // В любом исходе (успех, ошибка, отмена, таймаут) процесс уже остановлен в RunWorkerAsync — замок
            // снимается, временный звук удаляется, очередь освобождается. Освобождение очереди — в собственном
            // finally: застрявший семафор остановил бы ВСЕ будущие расшифровки.
            try
            {
                if (runLock is not null)
                {
                    await runLock.DisposeAsync().ConfigureAwait(false);
                }

                await DeleteWorkDirAsync(workDir).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }
    }

    /// <inheritdoc />
    public void Dispose() => _gate.Dispose();

    /// <summary>
    /// Таймаут прогона: <see cref="BaseRunTimeout"/> + <see cref="SpeechOptions.EffectiveTimeoutFactor"/> ×
    /// длительность записи; длительность неизвестна — берётся предел <see cref="SpeechOptions.MaxDuration"/>.
    /// </summary>
    /// <param name="audioDuration">Длительность подготовленного WAV или <see langword="null"/>.</param>
    /// <param name="options">Настройки (множитель, предел длительности).</param>
    public static TimeSpan ComputeRunTimeout(TimeSpan? audioDuration, SpeechOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var limit = options.MaxDuration;

        // Запись длиннее предела до распознавателя не доходит (отказ в FfmpegAudioExtractor); ограничение здесь —
        // чтобы таймаут при любом входе оставался в границах таймера CancelAfter.
        var duration = audioDuration is { } value && value > TimeSpan.Zero && value < limit ? value : limit;
        return BaseRunTimeout + duration * options.EffectiveTimeoutFactor;
    }

    private TimeSpan RunBudget(TimeSpan? audioDuration) => TimeoutOverride ?? ComputeRunTimeout(audioDuration, _options);

    // Текст для статуса носителя: сколько было отведено и из чего это сложилось, какой ключ крутить.
    private string TimeoutMessage(TimeSpan budget, TimeSpan? audioDuration)
    {
        var factor = _options.EffectiveTimeoutFactor.ToString(CultureInfo.InvariantCulture);
        var basis = audioDuration is { } duration
            ? $"длительность записи {FfmpegAudioExtractor.FormatDuration(duration)}"
            : $"предел длительности {FfmpegAudioExtractor.FormatDuration(_options.MaxDuration)}, пока длительность записи не была известна (ffprobe или ffmpeg не завершились)";
        return $"Расшифровка остановлена по таймауту: прогон не уложился в {FfmpegAudioExtractor.FormatDuration(budget)} "
            + $"({FfmpegAudioExtractor.FormatDuration(BaseRunTimeout)} + {SpeechConfigurationKeys.TimeoutFactor} = {factor} × {basis}). "
            + "Процесс-распознаватель и ffmpeg остановлены. Если сервер медленнее расчётного (мало ядер, занят другими "
            + $"задачами), увеличьте {SpeechConfigurationKeys.TimeoutFactor}; если зависает один и тот же файл — вероятно, он повреждён.";
    }

    /// <summary>
    /// Аргументы командной строки процесса-распознавателя (без пути к нему самому). Числа — в
    /// инвариантной культуре: на сервере с русской локалью «20,5» утилита не разобрала бы.
    /// </summary>
    /// <param name="modelPath">Проверенный путь к модели.</param>
    /// <param name="tokensPath">Проверенный путь к словарю.</param>
    /// <param name="vadPath">Проверенный путь к детектору речи.</param>
    /// <param name="wavPath">Подготовленный WAV.</param>
    /// <param name="options">Настройки (потоки, длина куска).</param>
    public static IReadOnlyList<string> BuildWorkerArguments(
        string modelPath, string tokensPath, string vadPath, string wavPath, SpeechOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var threads = Math.Clamp(options.Threads, 1, 64);
        var maxSeconds = Math.Clamp(options.MaxSegmentSeconds, SpeechOptions.MinAllowedSegmentSeconds, SpeechOptions.MaxAllowedSegmentSeconds);
        return
        [
            "--model", modelPath,
            "--tokens", tokensPath,
            "--vad", vadPath,
            "--input", wavPath,
            "--threads", threads.ToString(CultureInfo.InvariantCulture),
            "--max-segment-seconds", maxSeconds.ToString(CultureInfo.InvariantCulture),
        ];
    }

    private VerifiedFiles VerifyFiles()
    {
        if (string.IsNullOrWhiteSpace(_options.WorkerPath))
        {
            throw new InvalidOperationException(
                $"Расшифровка не настроена: не задан путь к процессу-распознавателю ({SpeechConfigurationKeys.WorkerPath}).");
        }

        var worker = Path.GetFullPath(_options.WorkerPath);
        if (!File.Exists(worker))
        {
            throw new FileNotFoundException(
                $"Процесс-распознаватель не найден: {worker}. Путь в конфигурации ({SpeechConfigurationKeys.WorkerPath}): «{_options.WorkerPath}»"
                + (Path.IsPathRooted(_options.WorkerPath) ? string.Empty : $" (относительный, считается от рабочего каталога {Directory.GetCurrentDirectory()})")
                + ". Утилита ISC.AI.Speech.Worker поставляется в СОБСТВЕННУЮ папку (не в каталог хоста: конфликт onnxruntime)"
                + " скриптом deploy/offline/publish-speech-worker.ps1; в разработке — её каталог сборки bin/<конфигурация>/net10.0.",
                worker);
        }

        return new VerifiedFiles(
            worker,
            SpeechModelIntegrity.EnsureTrusted(_options.ModelPath, _options.ModelSha256, "модель распознавания речи",
                SpeechConfigurationKeys.ModelPath, SpeechConfigurationKeys.ModelSha256),
            SpeechModelIntegrity.EnsureTrusted(_options.TokensPath, _options.TokensSha256, "словарь модели распознавания",
                SpeechConfigurationKeys.TokensPath, SpeechConfigurationKeys.TokensSha256),
            SpeechModelIntegrity.EnsureTrusted(_options.VadPath, _options.VadSha256, "детектор речи Silero VAD",
                SpeechConfigurationKeys.VadPath, SpeechConfigurationKeys.VadSha256));
    }

    // Запускает процесс, разбирает stdout, собирает фрагменты, в конце сверяет код выхода и итоговую строку
    // done — из неё же берётся длительность записи (длина WAV, а не конец последней речи).
    private async Task<AudioTranscription> RunWorkerAsync(VerifiedFiles files, string wavPath, CancellationToken cancellationToken)
    {
        var segments = new List<TranscriptSegmentDraft>();
        using var process = new Process { StartInfo = CreateStartInfo(files, wavPath) };
        var stderr = new BoundedTextTail(MaxStderrChars);
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                stderr.AppendLine(e.Data);
            }
        };

        try
        {
            process.Start();
        }
        catch (Win32Exception exception)
        {
            throw new InvalidOperationException(
                $"Не удалось запустить процесс-распознаватель {files.Worker}: {exception.Message}", exception);
        }

        process.BeginErrorReadLine();
        TryLowerPriority(process);

        var parser = new SpeechWorkerOutputParser();
        using (cancellationToken.Register(static state => KillTree((Process)state!), process))
        {
            try
            {
                while (await process.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
                {
                    TranscriptSegmentDraft? segment;
                    try
                    {
                        segment = parser.Accept(line);
                    }
                    catch (FormatException exception)
                    {
                        throw new InvalidOperationException(
                            "Процесс-распознаватель нарушил протокол: " + exception.Message, exception);
                    }

                    if (segment is not null)
                    {
                        segments.Add(segment);
                    }
                }

                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                // Выход по ошибке протокола или отмене: процесс ещё жив — остановить вместе с потомками
                // и дождаться выхода, иначе он держит временный WAV и тот не удалится.
                KillTree(process);
                await WaitForExitQuietlyAsync(process).ConfigureAwait(false);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (process.ExitCode != SpeechWorkerExitCodes.Success)
        {
            throw new InvalidOperationException(
                $"Расшифровка не выполнена: {SpeechWorkerExitCodes.Describe(process.ExitCode)}.{Diagnostics(stderr)}");
        }

        try
        {
            parser.EnsureCompleted();
        }
        catch (FormatException exception)
        {
            throw new InvalidOperationException(
                "Процесс-распознаватель нарушил протокол: " + exception.Message + Diagnostics(stderr), exception);
        }

        return new AudioTranscription(segments, parser.Done!.DurationMs); // EnsureCompleted гарантирует done
    }

    private ProcessStartInfo CreateStartInfo(VerifiedFiles files, string wavPath)
    {
        // Сборку .dll (framework-dependent без apphost, другая ОС) запускает dotnet; .exe — напрямую.
        var viaDotnet = files.Worker.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
        var info = new ProcessStartInfo(viaDotnet ? "dotnet" : files.Worker)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Utf8NoBom,
            StandardErrorEncoding = Utf8NoBom,
            WorkingDirectory = Path.GetDirectoryName(files.Worker) ?? string.Empty,
        };

        if (viaDotnet)
        {
            info.ArgumentList.Add(files.Worker);
        }

        foreach (var argument in BuildWorkerArguments(files.Model, files.Tokens, files.Vad, wavPath, _options))
        {
            info.ArgumentList.Add(argument);
        }

        return info;
    }

    private static string Diagnostics(BoundedTextTail stderr)
    {
        var text = stderr.ToString();
        return text.Length == 0 ? string.Empty : $" Диагностика процесса: {text}";
    }

    // Распознавание — фоновая работа: пониженный приоритет оставляет процессор интерактивному хосту.
    private static void TryLowerPriority(Process process)
    {
        try
        {
            process.PriorityClass = ProcessPriorityClass.BelowNormal;
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            // Нет прав или процесс уже завершился — приоритет не критичен.
        }
    }

    private static void KillTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // Процесс уже завершился между проверкой и остановкой.
        }
    }

    private static async Task WaitForExitQuietlyAsync(Process process)
    {
        try
        {
            await process.WaitForExitAsync(CancellationToken.None).WaitAsync(ExitWaitTimeout).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is TimeoutException or InvalidOperationException)
        {
            // Не завершился за отведённое время или уже освобождён — уборка попробует всё равно.
        }
    }

    /// <summary>
    /// Удаляет временный каталог прогона с несколькими попытками: только что остановленный процесс может
    /// ещё мгновение держать файл. Временный WAV — копия материала дела, поэтому неудача не глотается
    /// молча, а пишется в журнал приложения с путём (ТБ-064: гарантированное удаление должно быть
    /// проверяемым); остаток повторяется перед следующим прогоном (<see cref="SweepLeftovers"/>) и при
    /// старте хоста (<see cref="SpeechWorkDirSweeper"/>). Вызывается под очередью (_gate).
    /// </summary>
    private async Task DeleteWorkDirAsync(string path)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }

                return;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                if (attempt >= DeleteAttempts)
                {
                    SpeechTranscriberLog.WorkDirNotDeleted(_logger, exception, path);
                    _undeletedWorkDirs.Add(path);
                    return;
                }

                await Task.Delay(DeleteRetryDelay, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Уборка остатков перед прогоном (вызывается под очередью, каждый каталог — отдельно, неудача — в журнал):
    /// (1) СВОИ каталоги, которые не удалились сразу, — любого возраста: своих живых прогонов сейчас нет, очередь
    /// у этого прогона; (2) каталоги погибших процессов (<see cref="SpeechWorkDirSweeper.Sweep"/>) — по тем же
    /// правилам, что при старте хоста: не заняты замком и не менялись с момента старта этого процесса. Порог
    /// «старт процесса», а не прежние «сутки»: остатки погибшего хоста уходят при первой же расшифровке (на
    /// Windows при старте их мог ещё держать не завершившийся процесс-распознаватель), а живые прогоны другого
    /// экземпляра хоста защищены замком и свежестью.
    /// </summary>
    private void SweepLeftovers()
    {
        for (var i = _undeletedWorkDirs.Count - 1; i >= 0; i--)
        {
            var path = _undeletedWorkDirs[i];
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }

                _undeletedWorkDirs.RemoveAt(i);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                SpeechTranscriberLog.WorkDirNotDeleted(_logger, exception, path);
            }
        }

        SpeechWorkDirSweeper.Sweep(WorkRoot, _processStartUtc, _logger);
    }

    private sealed record VerifiedFiles(string Worker, string Model, string Tokens, string Vad);
}
