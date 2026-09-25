using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ISC.AI.Modules.Media.Domain.Model;
using ISC.AI.Vision.Onnx;
using ISC.AI.Vision.Onnx.Video;
using Microsoft.Extensions.Configuration;
using Shouldly;
using Xunit;

namespace ISC.AI.UnitTests.Vision;

/// <summary>
/// Переборка процессов вырезки кадра и пробы (ADR-0028; ревью 25.09.2026, п. 1): не больше
/// <see cref="VisionOptions.EffectiveMaxConcurrentFrameExtractions"/> внешних процессов одновременно, ожидание слота —
/// под таймаутом вырезки с явной ошибкой «сервер занят». Без ffmpeg: слот занимается ДО запуска процесса, а сам
/// запуск с несуществующим каталогом бинарников падает быстро и явно.
/// </summary>
public sealed class FfmpegFrameExtractorConcurrencyTests : IDisposable
{
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(20);

    private readonly string _file = Path.Combine(Path.GetTempPath(), "iscai-conc-" + Guid.NewGuid().ToString("N") + ".mp4");

    public FfmpegFrameExtractorConcurrencyTests()
    {
        File.WriteAllBytes(_file, [0]); // существует — проверки до слота проходят, а до ffmpeg дело доходит уже в слоте
    }

    public void Dispose() => File.Delete(_file);

    [Fact(DisplayName = "Предел 1: второй вызов (проба) не стартует, пока первый (вырезка) держит слот; после освобождения идёт следующим")]
    public async Task Calls_are_serialized_by_the_limit()
    {
        using var gate = new SemaphoreSlim(0);
        using var entered = new SemaphoreSlim(0);
        var starts = 0;
        var extractor = new FfmpegFrameExtractor(Options(limit: 1))
        {
            BeforeFrameProcessForTests = async token =>
            {
                Interlocked.Increment(ref starts);
                entered.Release();
                await gate.WaitAsync(token);
            },
        };

        var first = extractor.ExtractFrameAsync(_file, TimeSpan.Zero, FrameImageFormat.Png);
        (await entered.WaitAsync(Generous)).ShouldBeTrue("первый вызов занял слот");
        extractor.FreeFrameProcessSlots.ShouldBe(0);

        var second = extractor.ProbeAsync(_file);
        await Task.Delay(200);
        starts.ShouldBe(1, "второй вызов ждёт слот, а не идёт параллельно");
        second.IsCompleted.ShouldBeFalse();

        // Первый отпускаем: он доходит до ffmpeg, которого нет, — явная ошибка, слот освобождён, второй стартует.
        gate.Release();
        (await Should.ThrowAsync<InvalidOperationException>(() => first)).Message.ShouldStartWith("ffmpeg не вырезал кадр");
        (await entered.WaitAsync(Generous)).ShouldBeTrue("второй вызов получил слот");
        starts.ShouldBe(2);

        gate.Release();
        (await Should.ThrowAsync<InvalidOperationException>(() => second)).Message.ShouldStartWith("ffprobe не разобрал файл");
        extractor.FreeFrameProcessSlots.ShouldBe(1, "слоты возвращены и после ошибок");
    }

    [Fact(DisplayName = "Слот не освободился за таймаут — InvalidOperationException «сервер занят вырезкой кадров» с ключом конфигурации, не отмена")]
    public async Task Waiting_beyond_timeout_is_a_busy_error()
    {
        using var gate = new SemaphoreSlim(0);
        using var entered = new SemaphoreSlim(0);
        var extractor = new FfmpegFrameExtractor(Options(limit: 1))
        {
            FrameExtractionTimeoutOverride = TimeSpan.FromMilliseconds(300),
            // Держатель слота на дедлайн не смотрит (иначе освободил бы слот раньше, чем истёк дедлайн ждущего).
            BeforeFrameProcessForTests = async _ =>
            {
                entered.Release();
                await gate.WaitAsync();
            },
        };

        var holder = extractor.ExtractFrameAsync(_file, TimeSpan.Zero, FrameImageFormat.Png);
        (await entered.WaitAsync(Generous)).ShouldBeTrue();

        var busy = await Should.ThrowAsync<InvalidOperationException>(
            () => extractor.ExtractFrameAsync(_file, TimeSpan.Zero, FrameImageFormat.Jpeg, maxSide: 640));
        busy.Message.ShouldStartWith("Сервер занят вырезкой кадров");
        busy.Message.ShouldContain(VisionOptions.MaxConcurrentFrameExtractionsKey);
        extractor.FreeFrameProcessSlots.ShouldBe(0, "ждущий слот не занимал");

        // Держателя отпускаем: его дедлайн уже истёк — таймаут вырезки, слот возвращён.
        gate.Release();
        (await Should.ThrowAsync<InvalidOperationException>(() => holder)).Message.ShouldStartWith("ffmpeg не вырезал кадр за 0.3 с");
        extractor.FreeFrameProcessSlots.ShouldBe(1);
    }

    [Fact(DisplayName = "Отмена вызывающим во время ожидания слота — OperationCanceledException, слот не тронут")]
    public async Task Cancellation_while_waiting_is_cancellation()
    {
        using var gate = new SemaphoreSlim(0);
        using var entered = new SemaphoreSlim(0);
        var extractor = new FfmpegFrameExtractor(Options(limit: 1))
        {
            BeforeFrameProcessForTests = async token =>
            {
                entered.Release();
                await gate.WaitAsync(token);
            },
        };

        var holder = extractor.ProbeAsync(_file);
        (await entered.WaitAsync(Generous)).ShouldBeTrue();

        using var cancelled = new CancellationTokenSource();
        var waiting = extractor.ExtractFrameAsync(_file, TimeSpan.Zero, FrameImageFormat.Png, cancellationToken: cancelled.Token);
        await cancelled.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(() => waiting);

        gate.Release();
        await Should.ThrowAsync<InvalidOperationException>(() => holder);
        extractor.FreeFrameProcessSlots.ShouldBe(1);
    }

    [Fact(DisplayName = "Умолчание предела — половина ядер, не меньше 1; явное положительное значение берётся как есть; ключ Vision:Ffmpeg:MaxConcurrentFrameExtractions читается, мусор/ноль — умолчание")]
    public void Limit_defaults_and_configuration()
    {
        VisionOptions.DefaultMaxConcurrentFrameExtractions.ShouldBe(Math.Max(1, Environment.ProcessorCount / 2));
        Options(limit: null).EffectiveMaxConcurrentFrameExtractions.ShouldBe(VisionOptions.DefaultMaxConcurrentFrameExtractions);
        Options(limit: 0).EffectiveMaxConcurrentFrameExtractions.ShouldBe(VisionOptions.DefaultMaxConcurrentFrameExtractions);
        Options(limit: 3).EffectiveMaxConcurrentFrameExtractions.ShouldBe(3);

        VisionOptions.MaxConcurrentFrameExtractionsKey.ShouldBe("Vision:Ffmpeg:MaxConcurrentFrameExtractions");
        Read("4").MaxConcurrentFrameExtractions.ShouldBe(4);
        Read("0").MaxConcurrentFrameExtractions.ShouldBeNull();
        Read("много").MaxConcurrentFrameExtractions.ShouldBeNull();
        Read(null).MaxConcurrentFrameExtractions.ShouldBeNull();
        Read(null).EffectiveMaxConcurrentFrameExtractions.ShouldBe(VisionOptions.DefaultMaxConcurrentFrameExtractions);
    }

    private static VisionOptions Options(int? limit) =>
        new("d.onnx", "00", "e.onnx", "00", FfmpegFolder: "нет-такого-каталога", MaxConcurrentFrameExtractions: limit);

    private static VisionOptions Read(string? value)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new System.Collections.Generic.Dictionary<string, string?>
        {
            [VisionOptions.MaxConcurrentFrameExtractionsKey] = value,
        }).Build();
        return VisionOnnxServiceCollectionExtensions.ReadOptions(configuration);
    }
}
