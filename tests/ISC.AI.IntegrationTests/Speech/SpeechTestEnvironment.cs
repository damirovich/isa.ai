using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ISC.AI.Speech;
using Shouldly;

namespace ISC.AI.IntegrationTests.Speech;

/// <summary>
/// Поиск поставки для тестов расшифровки (ADR-0026): собранный процесс-распознаватель, ffmpeg и файлы
/// моделей с манифестом. Отсутствие любой части — явная ошибка с инструкцией, а не тихий пропуск:
/// «расшифровка не проверена» должно быть видно (как у FfmpegFrameExtractorTests).
/// </summary>
internal static class SpeechTestEnvironment
{
    /// <summary>
    /// Общая коллекция тестов расшифровки: классы идут последовательно — проверки уборки смотрят на общий
    /// каталог временных файлов, а процесс-распознаватель занимает процессор целиком.
    /// </summary>
    public const string CollectionName = "speech-worker";

    // Имена моделей в поставке (export-speech-models.ps1); при двух вариантах берётся первый — 220M.
    private static readonly string[] ModelFileNames =
        ["gigaam-multilingual-ctc-220m.int8.onnx", "gigaam-multilingual-ctc-600m.int8.onnx"];

    /// <summary>Подсказка, как прогнать остальные тесты без поставки.</summary>
    public const string ExcludeHint = "либо исключите категорию: dotnet test --filter \"Category!=Speech\"";

    /// <summary>Корень репозитория (каталог с ISC.AI.slnx).</summary>
    public static string RepositoryRoot { get; } = FindRepositoryRoot();

    /// <summary>
    /// Переменная окружения с путём к процессу-распознавателю (<c>.exe</c> или <c>.dll</c>; относительный — от
    /// корня репозитория): прогон тестов на ПОСТАВОЧНОЙ сборке, например
    /// <c>deploy/offline/speech-worker/win-x64/ISC.AI.Speech.Worker.exe</c> (no-tts sherpa-onnx, publish-speech-worker.ps1).
    /// </summary>
    public const string WorkerVariable = "ISCAI_SPEECH_WORKER";

    /// <summary>
    /// Процесс-распознаватель: путь из <see cref="WorkerVariable"/>, иначе — его СОБСТВЕННЫЙ каталог сборки
    /// <c>src/integrations/ISC.AI.Speech.Worker/bin/&lt;конфигурация&gt;/net10.0</c> (там лежит нативный sherpa-onnx из
    /// NuGet; в вывод тестов он не копируется намеренно — конфликт onnxruntime с распознаванием лиц). Тестовый
    /// проект утилиту НЕ собирает (ссылки на неё нет — утилита изолирована от решения): её собирают отдельно.
    /// </summary>
    public static string LocateWorker()
    {
        var fromVariable = Environment.GetEnvironmentVariable(WorkerVariable);
        if (!string.IsNullOrWhiteSpace(fromVariable))
        {
            var delivered = Path.GetFullPath(Path.Combine(RepositoryRoot, fromVariable.Trim()));
            if (!File.Exists(delivered))
            {
                throw new InvalidOperationException(
                    $"Процесс-распознаватель из переменной {WorkerVariable} не найден: {delivered}. Проверьте путь (поставка — "
                    + $"deploy/offline/publish-speech-worker.ps1) или уберите переменную, чтобы взять сборку разработчика; {ExcludeHint}.");
            }

            return delivered;
        }

        // Конфигурация сборки тестов (Debug/Release) — имя каталога над net10.0.
        var configuration = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar)).Parent?.Name ?? "Debug";
        var worker = Path.Combine(RepositoryRoot, "src", "integrations", "ISC.AI.Speech.Worker", "bin", configuration, "net10.0",
            OperatingSystem.IsWindows() ? "ISC.AI.Speech.Worker.exe" : "ISC.AI.Speech.Worker.dll");
        if (!File.Exists(worker))
        {
            throw new InvalidOperationException(
                $"Процесс-распознаватель не собран: {worker}. Тестовый проект его не собирает — выполните "
                + $"dotnet build src/integrations/ISC.AI.Speech.Worker -c {configuration} либо укажите готовую сборку поставки "
                + $"в переменной окружения {WorkerVariable}; {ExcludeHint}.");
        }

        return worker;
    }

    /// <summary>Поставка ffmpeg (<c>deploy/offline/ffmpeg/win-x64</c>, export-ffmpeg.ps1).</summary>
    public static string LocateFfmpegFolder()
    {
        var folder = Path.Combine(RepositoryRoot, "deploy", "offline", "ffmpeg", "win-x64");
        if (!File.Exists(Path.Combine(folder, "ffmpeg.exe")) || !File.Exists(Path.Combine(folder, "ffprobe.exe")))
        {
            throw new InvalidOperationException(
                $"Поставка ffmpeg не найдена: {folder} (нужны ffmpeg.exe и ffprobe.exe). Выполните deploy/offline/export-ffmpeg.ps1 "
                + $"(LGPL-сборка с пином SHA-256, ADR-0020/ТИ-004) {ExcludeHint}.");
        }

        return folder;
    }

    /// <summary>
    /// Настройки распознавания по офлайн-поставке моделей (<c>deploy/offline/models/speech</c>,
    /// export-speech-models.ps1): пути и пины SHA-256 — из манифеста <c>speech-models.sha256</c>, то есть ровно
    /// то, что хост получил бы в конфигурации. Если поставлены обе модели, берётся 220M.
    /// </summary>
    public static SpeechOptions LoadDeliveredOptions(string worker, string ffmpegFolder)
    {
        var dir = Path.Combine(RepositoryRoot, "deploy", "offline", "models", "speech");
        var manifest = Path.Combine(dir, "speech-models.sha256");
        if (!File.Exists(manifest))
        {
            throw new InvalidOperationException(
                $"Модели распознавания речи не поставлены: нет {manifest}. Выполните на машине с интернетом "
                + "deploy/offline/export-speech-models.ps1 (GigaAM Multilingual MIT + Silero VAD MIT, пины SHA-256, ADR-0026; "
                + $"скачивание моделей — с разрешения заказчика) {ExcludeHint}.");
        }

        var pins = File.ReadAllLines(manifest, Encoding.UTF8)
            .Select(line => line.Trim().TrimStart('﻿'))
            .Where(line => line.Length > 0)
            .Select(line => line.Split("  ", 2))
            .ToDictionary(parts => parts[1].Trim(), parts => parts[0].Trim(), StringComparer.OrdinalIgnoreCase);

        var model = ModelFileNames.FirstOrDefault(pins.ContainsKey)
            ?? throw new InvalidOperationException($"В манифесте {manifest} нет модели gigaam-multilingual-ctc-*.int8.onnx — повторите export-speech-models.ps1.");

        return new SpeechOptions(
            WorkerPath: worker,
            ModelPath: Path.Combine(dir, model),
            ModelSha256: Pin(pins, "model", model, manifest),
            TokensPath: Path.Combine(dir, "tokens.txt"),
            TokensSha256: Pin(pins, "tokens", "tokens.txt", manifest),
            VadPath: Path.Combine(dir, "silero_vad.onnx"),
            VadSha256: Pin(pins, "vad", "silero_vad.onnx", manifest),
            FfmpegFolder: ffmpegFolder,
            Threads: 2);
    }

    /// <summary>Запускает ffmpeg поставки для подготовки тестового файла (исходники генерирует сам ffmpeg — медиафайлов в репозитории нет).</summary>
    public static async Task RunFfmpegAsync(string ffmpegFolder, string arguments)
    {
        var info = new ProcessStartInfo(Path.Combine(ffmpegFolder, "ffmpeg.exe"), "-hide_banner -loglevel error " + arguments)
        {
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        using var process = Process.Start(info) ?? throw new InvalidOperationException("Не удалось запустить ffmpeg.");
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        process.ExitCode.ShouldBe(0, $"подготовка файла: ffmpeg {arguments}{Environment.NewLine}{error}");
    }

    /// <summary>Итог прямого запуска процесса-распознавателя.</summary>
    public sealed record WorkerRun(int ExitCode, IReadOnlyList<string> StdoutLines, string Stderr);

    /// <summary>
    /// Запускает процесс-распознаватель напрямую (проверка протокола и кодов выхода); сборку <c>.dll</c> — через
    /// <c>dotnet</c>, как адаптер.
    /// </summary>
    public static async Task<WorkerRun> RunWorkerAsync(string worker, params string[] arguments)
    {
        var viaDotnet = worker.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
        var info = new ProcessStartInfo(viaDotnet ? "dotnet" : worker)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        if (viaDotnet)
        {
            info.ArgumentList.Add(worker);
        }

        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        using var process = Process.Start(info) ?? throw new InvalidOperationException("Не удалось запустить процесс-распознаватель.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        var lines = (await stdout).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return new WorkerRun(process.ExitCode, lines, await stderr);
    }

    /// <summary>WAV 16 кГц моно 16 бит из отсчётов — без ffmpeg (для проверок, где звук не важен).</summary>
    public static byte[] Pcm16MonoWave(int sampleCount)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8);
        writer.Write(36 + sampleCount * 2);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(16000);
        writer.Write(32000);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(sampleCount * 2);
        writer.Write(new byte[sampleCount * 2]);
        writer.Flush();
        return stream.ToArray();
    }

    /// <summary>Формат и объём данных WAV из заголовка.</summary>
    public sealed record WaveInfo(int SampleRate, int Channels, int BitsPerSample, long DataBytes)
    {
        /// <summary>Длительность по объёму данных.</summary>
        public TimeSpan Duration => TimeSpan.FromSeconds((double)DataBytes / (SampleRate * Channels * (BitsPerSample / 8)));
    }

    /// <summary>
    /// Читает заголовок WAV независимо от кода адаптера: блоки RIFF по порядку (ffmpeg пишет ещё и LIST), формат —
    /// из fmt, объём данных — от начала data до конца файла.
    /// </summary>
    public static WaveInfo ReadWave(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream, Encoding.ASCII);
        Encoding.ASCII.GetString(reader.ReadBytes(4)).ShouldBe("RIFF");
        reader.ReadUInt32();
        Encoding.ASCII.GetString(reader.ReadBytes(4)).ShouldBe("WAVE");

        int? rate = null, channels = null, bits = null;
        while (stream.Position + 8 <= stream.Length)
        {
            var id = Encoding.ASCII.GetString(reader.ReadBytes(4));
            var size = reader.ReadUInt32();
            if (id == "fmt ")
            {
                var start = stream.Position;
                reader.ReadUInt16().ShouldBe((ushort)1, "PCM без сжатия");
                channels = reader.ReadUInt16();
                rate = reader.ReadInt32();
                reader.ReadInt32();
                reader.ReadUInt16();
                bits = reader.ReadUInt16();
                stream.Position = start + size;
            }
            else if (id == "data")
            {
                rate.ShouldNotBeNull("блок fmt идёт до data");
                return new WaveInfo(rate.Value, channels!.Value, bits!.Value, stream.Length - stream.Position);
            }
            else
            {
                stream.Seek(size + (size & 1), SeekOrigin.Current);
            }
        }

        throw new InvalidOperationException($"В WAV нет блока data: {path}");
    }

    private static string Pin(Dictionary<string, string> pins, string purpose, string name, string manifest) =>
        pins.TryGetValue(name, out var sha)
            ? sha
            : throw new InvalidOperationException($"В манифесте {manifest} нет файла {name} ({purpose}) — повторите export-speech-models.ps1.");

    private static string FindRepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ISC.AI.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Корень репозитория (ISC.AI.slnx) не найден от каталога тестов.");
    }
}
