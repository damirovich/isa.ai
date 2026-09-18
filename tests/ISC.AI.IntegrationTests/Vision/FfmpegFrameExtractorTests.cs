using System;
using System.Collections.Generic;
using System.Diagnostics;
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
/// Раскадровка видео НАСТОЯЩИМ ffmpeg (ТФ-МЕД-02, ТО-мат-06): единственная часть конвейера, которую
/// нельзя проверить без внешнего бинарника — остальное покрыто юнит-тестами на синтетике.
/// Тестовое видео генерируется тем же ffmpeg (<c>testsrc2</c>), поэтому в репозитории нет ни одного
/// медиафайла, а реальные съёмки людей в тесты не попадают (ТО-прог-13).
/// </summary>
/// <remarks>
/// Требуется поставка ffmpeg: <c>deploy/offline/ffmpeg/win-x64</c> (скрипт
/// <c>deploy/offline/export-ffmpeg.ps1</c>, ТИ-004, ADR-0020). Без неё тест падает с инструкцией —
/// намеренно, вместо тихого пропуска: «видео не проверено» должно быть видно. Прогон без поставки:
/// <c>dotnet test --filter "Category!=Video"</c>.
/// </remarks>
[Trait("Category", "Video")]
public sealed class FfmpegFrameExtractorTests : IAsyncLifetime
{
    private const int ClipSeconds = 6;
    private const int ClipFps = 25;

    private readonly string _workDir = Path.Combine(
        Path.GetTempPath(), "iscai-video-tests", Guid.NewGuid().ToString("N"));

    private string _ffmpegFolder = string.Empty;
    private string _mp4Path = string.Empty;
    private string _webmPath = string.Empty;

    /// <summary>Готовит два клипа (MPEG-4 и VP9/WebM) — проверяем не кодек, а раскадровку.</summary>
    public async Task InitializeAsync()
    {
        _ffmpegFolder = LocateFfmpegFolder();
        Directory.CreateDirectory(_workDir);

        _mp4Path = Path.Combine(_workDir, "clip.mp4");
        _webmPath = Path.Combine(_workDir, "clip.webm");

        // mpeg4 и libvpx-vp9 — кодеки БЕЗ обязательств GPL: в LGPL-сборке x264/x265 отключены (ADR-0020).
        await RunFfmpegAsync($"-y -f lavfi -i testsrc2=size=320x240:rate={ClipFps} -t {ClipSeconds} -c:v mpeg4 -q:v 5 \"{_mp4Path}\"");
        await RunFfmpegAsync($"-y -f lavfi -i testsrc2=size=320x240:rate={ClipFps} -t {ClipSeconds} -c:v libvpx-vp9 -b:v 300k \"{_webmPath}\"");
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

    [Fact(DisplayName = "ТО-мат-06: 1 кадр/с даёт кадры с последовательными индексами и таймкодами по секундам; каждый кадр — целый JPEG")]
    public async Task Extracts_one_frame_per_second_with_timecodes()
    {
        var extractor = new FfmpegFrameExtractor(Options());

        var frames = new List<VideoFrame>();
        await foreach (var frame in extractor.ExtractAsync(_mp4Path, new FrameSamplingOptions(1.0)))
        {
            frames.Add(frame);
        }

        // Клип 6 с при 1 к/с: ffmpeg отдаёт 6 кадров (границы секунд), допускаем 7-й на последнем кадре.
        frames.Count.ShouldBeInRange(ClipSeconds, ClipSeconds + 1);
        frames.Select(f => f.Index).ShouldBe(Enumerable.Range(0, frames.Count));

        for (var i = 0; i < frames.Count; i++)
        {
            frames[i].Timestamp.ShouldBe(TimeSpan.FromSeconds(i), $"таймкод кадра {i}");

            // Целостность кадра: JPEG начинается SOI (FF D8) и заканчивается EOI (FF D9) — именно по этим
            // маркерам MjpegStreamSplitter режет поток, и обрезанный кадр означал бы потерю лица.
            var jpeg = frames[i].JpegBytes;
            jpeg.Length.ShouldBeGreaterThan(1024, $"размер кадра {i}");
            jpeg[0].ShouldBe((byte)0xFF);
            jpeg[1].ShouldBe((byte)0xD8);
            jpeg[^2].ShouldBe((byte)0xFF);
            jpeg[^1].ShouldBe((byte)0xD9);
        }
    }

    [Fact(DisplayName = "ТО-мат-06: частота выборки и предел числа кадров соблюдаются; контейнер WebM раскадровывается так же")]
    public async Task Respects_sampling_rate_and_frame_limit()
    {
        var extractor = new FfmpegFrameExtractor(Options());

        // 2 к/с на 6-секундном клипе — вдвое больше кадров, таймкоды через полсекунды.
        var dense = new List<VideoFrame>();
        await foreach (var frame in extractor.ExtractAsync(_mp4Path, new FrameSamplingOptions(2.0)))
        {
            dense.Add(frame);
        }

        dense.Count.ShouldBeGreaterThan(ClipSeconds);
        dense[2].Timestamp.ShouldBe(TimeSpan.FromSeconds(1));

        // MaxFrames обрывает чтение: длинное видео не читается целиком (ТО-мат-06, память конвейера).
        var limited = new List<VideoFrame>();
        await foreach (var frame in extractor.ExtractAsync(_mp4Path, new FrameSamplingOptions(1.0, MaxFrames: 2)))
        {
            limited.Add(frame);
        }

        limited.Count.ShouldBe(2);

        // Тот же конвейер на VP9/WebM: контейнер значения не имеет, декодирует ffmpeg.
        var webm = new List<VideoFrame>();
        await foreach (var frame in extractor.ExtractAsync(_webmPath, new FrameSamplingOptions(1.0)))
        {
            webm.Add(frame);
        }

        webm.Count.ShouldBeInRange(ClipSeconds, ClipSeconds + 1);
        webm[0].JpegBytes.Length.ShouldBeGreaterThan(1024);
    }

    [Fact(DisplayName = "Отсутствующий файл видео — явная ошибка до запуска ffmpeg; отмена прекращает раскадровку")]
    public async Task Missing_file_and_cancellation_are_explicit()
    {
        var extractor = new FfmpegFrameExtractor(Options());

        await Should.ThrowAsync<FileNotFoundException>(async () =>
        {
            await foreach (var _ in extractor.ExtractAsync(Path.Combine(_workDir, "нет-такого.mp4"), new FrameSamplingOptions(1.0)))
            {
                // до первой итерации дело не доходит
            }
        });

        using var cts = new CancellationTokenSource();
        var read = 0;
        await Should.ThrowAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in extractor.ExtractAsync(_mp4Path, new FrameSamplingOptions(1.0), cts.Token))
            {
                read++;
                await cts.CancelAsync();
            }
        });

        // Проверяется ПРЕКРАЩЕНИЕ, а не точное число кадров: между отменой и следующей итерацией лежит
        // буфер конвейера (ffmpeg уже записал кадр в поток, MjpegStreamSplitter уже его собрал), поэтому
        // один «лишний» кадр после отмены — нормальная работа, а не утечка. Существенно то, что клип
        // НЕ дочитывается до конца: иначе отмена долгой раскадровки ничего бы не экономила.
        read.ShouldBeInRange(1, ClipSeconds - 1);
    }

    private VisionOptions Options() =>
        new("d.onnx", "00", "e.onnx", "00", FfmpegFolder: _ffmpegFolder);

    /// <summary>Папка поставки ffmpeg; её отсутствие — явная ошибка с инструкцией, а не тихий пропуск.</summary>
    private static string LocateFfmpegFolder()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ISC.AI.slnx")))
        {
            dir = dir.Parent;
        }

        var folder = Path.Combine(dir?.FullName ?? ".", "deploy", "offline", "ffmpeg", "win-x64");
        if (!File.Exists(Path.Combine(folder, "ffmpeg.exe")))
        {
            throw new InvalidOperationException(
                $"Поставка ffmpeg не найдена: {folder}. Выполните deploy/offline/export-ffmpeg.ps1 "
                + "(LGPL-сборка с пином SHA-256, ADR-0020/ТИ-004) либо исключите категорию: "
                + "dotnet test --filter \"Category!=Video\".");
        }

        return folder;
    }

    private async Task RunFfmpegAsync(string arguments)
    {
        var info = new ProcessStartInfo(Path.Combine(_ffmpegFolder, "ffmpeg.exe"), "-hide_banner -loglevel error " + arguments)
        {
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        using var process = Process.Start(info) ?? throw new InvalidOperationException("Не удалось запустить ffmpeg.");
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        process.ExitCode.ShouldBe(0, $"подготовка клипа: ffmpeg {arguments}{Environment.NewLine}{error}");
    }
}
