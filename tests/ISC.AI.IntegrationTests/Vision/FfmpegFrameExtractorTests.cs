using System;
using System.Collections.Generic;
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
/// Поставка ffmpeg и генерация клипов — <see cref="VideoTestEnvironment"/>; без поставки тест падает с инструкцией —
/// намеренно, вместо тихого пропуска: «видео не проверено» должно быть видно. Прогон без поставки:
/// <c>dotnet test --filter "Category!=Video"</c>.
/// </remarks>
[Trait("Category", "Video")]
public sealed class FfmpegFrameExtractorTests : IAsyncLifetime
{
    private const int ClipSeconds = 6;
    private const int ClipFps = 25;

    private string _workDir = string.Empty;
    private string _ffmpegFolder = string.Empty;
    private string _mp4Path = string.Empty;
    private string _webmPath = string.Empty;
    private string _voicePath = string.Empty;

    /// <summary>
    /// Готовит два клипа (MPEG-4 и VP9/WebM) — проверяем не кодек, а раскадровку — и «голосовое» 3GP без
    /// картинки (только звук): такое браузер объявляет video/3gpp (ADR-0026).
    /// </summary>
    public async Task InitializeAsync()
    {
        _ffmpegFolder = VideoTestEnvironment.LocateFfmpegFolder();
        _workDir = VideoTestEnvironment.CreateWorkDir();

        _mp4Path = Path.Combine(_workDir, "clip.mp4");
        _webmPath = Path.Combine(_workDir, "clip.webm");
        _voicePath = Path.Combine(_workDir, "voice.3gp");

        // mpeg4 и libvpx-vp9 — кодеки БЕЗ обязательств GPL: в LGPL-сборке x264/x265 отключены (ADR-0020).
        await RunFfmpegAsync($"-y -f lavfi -i testsrc2=size=320x240:rate={ClipFps} -t {ClipSeconds} -c:v mpeg4 -q:v 5 \"{_mp4Path}\"");
        await RunFfmpegAsync($"-y -f lavfi -i testsrc2=size=320x240:rate={ClipFps} -t {ClipSeconds} -c:v libvpx-vp9 -b:v 300k \"{_webmPath}\"");

        // Встроенный кодер AAC ffmpeg (LGPL): тон 3 с в контейнере 3GP, видеопотока нет.
        await RunFfmpegAsync($"-y -f lavfi -i sine=frequency=440:duration=3 -c:a aac -b:a 32k \"{_voicePath}\"");
    }

    /// <inheritdoc />
    public Task DisposeAsync()
    {
        VideoTestEnvironment.TryDeleteWorkDir(_workDir);
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

    [Fact(DisplayName = "ADR-0026: голосовое .3gp без картинки — проба «видеопотока нет», раскадровка даёт ноль кадров без ошибки ffmpeg; у видеоклипов поток есть")]
    public async Task Audio_only_container_has_no_video_stream_and_yields_no_frames()
    {
        var extractor = new FfmpegFrameExtractor(Options());

        (await extractor.HasVideoStreamAsync(_voicePath)).ShouldBeFalse();
        (await extractor.HasVideoStreamAsync(_mp4Path)).ShouldBeTrue();
        (await extractor.HasVideoStreamAsync(_webmPath)).ShouldBeTrue();

        // Второй рубеж: даже если раскадровку позвали, ffmpeg на входе без картинки не запускается — кадров
        // просто нет (без него было бы «Output file does not contain any stream» и «ошибка обработки»).
        var frames = new List<VideoFrame>();
        await foreach (var frame in extractor.ExtractAsync(_voicePath, new FrameSamplingOptions(1.0)))
        {
            frames.Add(frame);
        }

        frames.ShouldBeEmpty();
    }

    private VisionOptions Options() =>
        new("d.onnx", "00", "e.onnx", "00", FfmpegFolder: _ffmpegFolder);

    private Task RunFfmpegAsync(string arguments) => VideoTestEnvironment.RunFfmpegAsync(_ffmpegFolder, arguments);
}
