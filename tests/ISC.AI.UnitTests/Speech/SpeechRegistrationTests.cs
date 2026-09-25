using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ISC.AI.Modules.Media.Domain.Services;
using ISC.AI.Speech;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Xunit;

namespace ISC.AI.UnitTests.Speech;

/// <summary>
/// Регистрация и настройки распознавания речи (ADR-0026): явный отказ «не настроена» вместо тихого пропуска,
/// выбор реализации по заполненности ключей, чтение секции <c>Speech</c>, версия модели, аргументы процесса.
/// Всё — без моделей, ffmpeg и процесса-распознавателя.
/// </summary>
public sealed class SpeechRegistrationTests
{
    private const string ModelPin = "2D94F93FFD4EF58E7899C9DE885C25BBBC8C9F1073618868D118A674450BA5F7";

    private static readonly string[] RequiredKeys =
    [
        "Speech:Worker:Path", "Speech:Model:Path", "Speech:Model:Sha256", "Speech:Tokens:Path",
        "Speech:Tokens:Sha256", "Speech:Vad:Path", "Speech:Vad:Sha256",
    ];

    [Fact(DisplayName = "ADR-0026: без настройки — отказ «Расшифровка не настроена» со всеми обязательными ключами, а не ноль фрагментов")]
    public async Task Unconfigured_transcriber_refuses_explicitly()
    {
        var transcriber = Resolve(new Dictionary<string, string?>());

        transcriber.ShouldBeOfType<UnconfiguredAudioTranscriber>();
        transcriber.ModelVersion.ShouldBe(UnconfiguredAudioTranscriber.NotConfiguredModelVersion);

        // Отказ — в задаче (как у рабочей реализации), сам вызов не бросает.
        var run = transcriber.TranscribeAsync("запись.ogg");
        var error = await Should.ThrowAsync<InvalidOperationException>(() => run);

        error.Message.ShouldStartWith("Расшифровка не настроена");
        foreach (var key in RequiredKeys)
        {
            error.Message.ShouldContain(key);
        }
    }

    [Fact(DisplayName = "Частичная настройка — в отказе перечислены только недостающие ключи")]
    public async Task Partially_configured_lists_only_missing_keys()
    {
        var transcriber = Resolve(new Dictionary<string, string?>
        {
            ["Speech:Worker:Path"] = "worker.exe",
            ["Speech:Model:Path"] = "model.onnx",
            ["Speech:Model:Sha256"] = ModelPin,
            ["Speech:Tokens:Path"] = "tokens.txt",
            ["Speech:Tokens:Sha256"] = "AA",
            ["Speech:Vad:Path"] = "silero_vad.onnx",
        });

        var error = await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            await transcriber.TranscribeAsync("запись.ogg");
        });

        error.Message.ShouldContain("Speech:Vad:Sha256");
        error.Message.ShouldNotContain("Speech:Model:Path");
        error.Message.ShouldNotContain("Speech:Worker:Path");
    }

    [Fact(DisplayName = "Полная настройка — рабочая реализация; регистрация диск не трогает (файлов может ещё не быть)")]
    public void Fully_configured_registers_worker_transcriber()
    {
        var transcriber = Resolve(FullConfiguration());

        transcriber.ShouldBeOfType<SpeechWorkerTranscriber>();
        transcriber.ModelVersion.ShouldBe("gigaam-multilingual-ctc@2d94f93ffd4e");
    }

    [Fact(DisplayName = "ModelVersion: имя@первые 12 символов пина модели строчными; своё имя модели из конфигурации")]
    public void Model_version_format()
    {
        SpeechOptions.FormatModelVersion("gigaam-multilingual-ctc", ModelPin).ShouldBe("gigaam-multilingual-ctc@2d94f93ffd4e");
        SpeechOptions.FormatModelVersion(null, " abc ").ShouldBe("gigaam-multilingual-ctc@abc");
        SpeechOptions.FormatModelVersion("vosk-ky-0.42", "").ShouldBe("vosk-ky-0.42@unpinned");

        var options = SpeechServiceCollectionExtensions.ReadOptions(Configuration(new Dictionary<string, string?>(FullConfiguration())
        {
            ["Speech:Model:Name"] = "gigaam-multilingual-large-ctc",
        }));
        options.ModelVersion.ShouldBe("gigaam-multilingual-large-ctc@2d94f93ffd4e");
    }

    [Fact(DisplayName = "Каталог ffmpeg: Speech:Ffmpeg:Folder, пусто — Vision:Ffmpeg:Folder (одна поставка на оба конвейера)")]
    public void Ffmpeg_folder_falls_back_to_vision()
    {
        SpeechServiceCollectionExtensions.ReadOptions(Configuration(new Dictionary<string, string?>
        {
            ["Vision:Ffmpeg:Folder"] = @"D:\ffmpeg\win-x64",
        })).FfmpegFolder.ShouldBe(@"D:\ffmpeg\win-x64");

        SpeechServiceCollectionExtensions.ReadOptions(Configuration(new Dictionary<string, string?>
        {
            ["Speech:Ffmpeg:Folder"] = @"E:\speech-ffmpeg",
            ["Vision:Ffmpeg:Folder"] = @"D:\ffmpeg\win-x64",
        })).FfmpegFolder.ShouldBe(@"E:\speech-ffmpeg");

        SpeechServiceCollectionExtensions.ReadOptions(Configuration(new Dictionary<string, string?>()))
            .FfmpegFolder.ShouldBeNull();
    }

    [Theory(DisplayName = "Потоки и длина куска: умолчания при отсутствии и мусоре, длина куска — не больше предела модели (25 с)")]
    [InlineData(null, null, 4, 20.0)]
    [InlineData("мусор", "двадцать", 4, 20.0)]
    [InlineData("0", "-3", 4, 20.0)]
    [InlineData("8", "12.5", 8, 12.5)]
    [InlineData("500", "90", 64, 25.0)]
    [InlineData("2", "0.5", 2, 2.0)]
    public void Numeric_settings_are_safe(string? threads, string? seconds, int expectedThreads, double expectedSeconds)
    {
        var options = SpeechServiceCollectionExtensions.ReadOptions(Configuration(new Dictionary<string, string?>
        {
            ["Speech:Threads"] = threads,
            ["Speech:MaxSegmentSeconds"] = seconds,
        }));

        options.Threads.ShouldBe(expectedThreads);
        options.MaxSegmentSeconds.ShouldBe(expectedSeconds);
    }

    [Theory(DisplayName = "ADR-0026: предел длительности (Speech:MaxDurationHours) и множитель таймаута (Speech:TimeoutFactor) — умолчания 24 ч и 3, границы 1–36 ч и 0,5–20")]
    [InlineData(null, null, 24.0, 3.0)]
    [InlineData("мусор", "втрое", 24.0, 3.0)]
    [InlineData("0", "-2", 24.0, 3.0)]
    [InlineData("NaN", "Infinity", 24.0, 3.0)]
    [InlineData("12", "5", 12.0, 5.0)]
    [InlineData("1.5", "2.5", 1.5, 2.5)]
    [InlineData("100", "1000", 36.0, 20.0)]
    [InlineData("0.25", "0.1", 1.0, 0.5)]
    public void Duration_limit_and_timeout_factor_are_bound(string? hours, string? factor, double expectedHours, double expectedFactor)
    {
        var options = SpeechServiceCollectionExtensions.ReadOptions(Configuration(new Dictionary<string, string?>
        {
            ["Speech:MaxDurationHours"] = hours,
            ["Speech:TimeoutFactor"] = factor,
        }));

        options.MaxDurationHours.ShouldBe(expectedHours);
        options.TimeoutFactor.ShouldBe(expectedFactor);
        options.MaxDuration.ShouldBe(TimeSpan.FromHours(expectedHours));
        options.EffectiveTimeoutFactor.ShouldBe(expectedFactor);
    }

    [Fact(DisplayName = "Настройки, созданные в обход конфигурации, всё равно в границах: предел не больше 36 ч (int32 детектора речи), нечисло — умолчание")]
    public void Directly_created_options_stay_in_range()
    {
        var options = new SpeechOptions("w.exe", "m", "00", "t", "00", "v", "00");
        options.MaxDuration.ShouldBe(TimeSpan.FromHours(24));
        options.EffectiveTimeoutFactor.ShouldBe(3);

        (options with { MaxDurationHours = 48, TimeoutFactor = 100 }).MaxDuration.ShouldBe(TimeSpan.FromHours(36));
        (options with { MaxDurationHours = 48, TimeoutFactor = 100 }).EffectiveTimeoutFactor.ShouldBe(20);
        (options with { MaxDurationHours = double.NaN, TimeoutFactor = double.NaN }).MaxDuration.ShouldBe(TimeSpan.FromHours(24));
        (options with { MaxDurationHours = double.NaN, TimeoutFactor = double.NaN }).EffectiveTimeoutFactor.ShouldBe(3);
        (options with { MaxDurationHours = 0, TimeoutFactor = 0 }).MaxDuration.ShouldBe(TimeSpan.FromHours(1));
    }

    [Fact(DisplayName = "ТБ-064: уборка остатков прошлых прогонов при старте хоста регистрируется и без настройки расшифровки, и с ней")]
    public void Startup_sweep_is_registered_in_both_branches()
    {
        foreach (var values in new[] { new Dictionary<string, string?>(), FullConfiguration() })
        {
            var services = new ServiceCollection();
            services.AddSpeechTranscription(Configuration(values));

            services.Count(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(SpeechWorkDirSweeper))
                .ShouldBe(1);
        }
    }

    [Fact(DisplayName = "Ключи конфигурации: полный список секции Speech для манифеста модуля")]
    public void Configuration_keys_are_complete()
    {
        SpeechConfigurationKeys.All.ShouldBeUnique();
        SpeechConfigurationKeys.All.ShouldAllBe(key => key.StartsWith("Speech:", StringComparison.Ordinal));
        foreach (var key in RequiredKeys.Concat(
                     ["Speech:Threads", "Speech:MaxSegmentSeconds", "Speech:Ffmpeg:Folder", "Speech:Model:Name",
                      "Speech:MaxDurationHours", "Speech:TimeoutFactor"]))
        {
            SpeechConfigurationKeys.All.ShouldContain(key);
        }
    }

    [Fact(DisplayName = "Аргументы процесса: числа в инвариантной культуре даже на сервере с русской локалью; границы соблюдены")]
    public void Worker_arguments_use_invariant_culture()
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ru-RU");
        try
        {
            var options = new SpeechOptions("w.exe", "m", "00", "t", "00", "v", "00", Threads: 100, MaxSegmentSeconds: 12.5);
            var args = SpeechWorkerTranscriber.BuildWorkerArguments(@"C:\m.onnx", @"C:\t.txt", @"C:\v.onnx", @"C:\a.wav", options);

            args.ShouldBe(new[]
            {
                "--model", @"C:\m.onnx", "--tokens", @"C:\t.txt", "--vad", @"C:\v.onnx", "--input", @"C:\a.wav",
                "--threads", "64", "--max-segment-seconds", "12.5",
            });
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact(DisplayName = "Нет исходного файла — FileNotFoundException до запуска чего-либо")]
    public async Task Missing_source_file_is_explicit()
    {
        using var transcriber = new SpeechWorkerTranscriber(new SpeechOptions("w.exe", "m", "00", "t", "00", "v", "00"));
        var missing = Path.Combine(Path.GetTempPath(), "нет-записи-" + Guid.NewGuid().ToString("N") + ".ogg");

        await Should.ThrowAsync<FileNotFoundException>(async () =>
        {
            await transcriber.TranscribeAsync(missing);
        });
    }

    [Fact(DisplayName = "ТИ-004: неверный пин модели — отказ ДО извлечения звука и запуска процесса; повторный вызов не зависает")]
    public async Task Wrong_pin_refuses_before_any_process()
    {
        var dir = Path.Combine(Path.GetTempPath(), "iscai-speech-unit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var source = Write(dir, "запись.ogg", [1, 2, 3]);
            var worker = Write(dir, "ISC.AI.Speech.Worker.exe", [0]);
            var model = Write(dir, "model.onnx", [5, 5, 5]);
            var tokens = Write(dir, "tokens.txt", [6]);
            var vad = Write(dir, "silero_vad.onnx", [7]);
            using var transcriber = new SpeechWorkerTranscriber(new SpeechOptions(
                worker, model, "DEADBEEF", tokens, "00", vad, "00", FfmpegFolder: Path.Combine(dir, "нет-ffmpeg")));

            // Дважды: очередь (семафор) освобождается и после отказа — второй вызов не ждёт вечно.
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var error = await Should.ThrowAsync<InvalidOperationException>(async () =>
                {
                    await transcriber.TranscribeAsync(source);
                });

                error.Message.ShouldContain("Целостность файла «модель распознавания речи»");
                error.Message.ShouldContain("Speech:Model:Sha256");
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact(DisplayName = "Нет процесса-распознавателя — отказ с полным путём и ключом Speech:Worker:Path")]
    public async Task Missing_worker_is_explicit()
    {
        var dir = Path.Combine(Path.GetTempPath(), "iscai-speech-unit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var source = Write(dir, "запись.ogg", [1]);
            var worker = Path.Combine(dir, "нет", "ISC.AI.Speech.Worker.exe");
            using var transcriber = new SpeechWorkerTranscriber(new SpeechOptions(worker, "m", "00", "t", "00", "v", "00"));

            var error = await Should.ThrowAsync<FileNotFoundException>(async () =>
            {
                await transcriber.TranscribeAsync(source);
            });

            error.Message.ShouldContain(worker);
            error.Message.ShouldContain("Speech:Worker:Path");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static IAudioTranscriber Resolve(IDictionary<string, string?> values)
    {
        var services = new ServiceCollection();
        services.AddSpeechTranscription(Configuration(values));
        return services.BuildServiceProvider().GetRequiredService<IAudioTranscriber>();
    }

    private static IConfiguration Configuration(IDictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private static Dictionary<string, string?> FullConfiguration() => new()
    {
        ["Speech:Worker:Path"] = "worker.exe",
        ["Speech:Model:Path"] = "model.onnx",
        ["Speech:Model:Sha256"] = ModelPin,
        ["Speech:Tokens:Path"] = "tokens.txt",
        ["Speech:Tokens:Sha256"] = "AA",
        ["Speech:Vad:Path"] = "silero_vad.onnx",
        ["Speech:Vad:Sha256"] = "BB",
    };

    private static string Write(string dir, string name, byte[] content)
    {
        var path = Path.Combine(dir, name);
        File.WriteAllBytes(path, content);
        return path;
    }
}
