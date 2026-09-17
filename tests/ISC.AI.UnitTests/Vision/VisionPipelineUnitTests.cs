using System;
using System.IO;
using System.Linq;
using ISC.AI.Modules.Media.Domain.Model;
using ISC.AI.Vision.Onnx;
using ISC.AI.Vision.Onnx.Quality;
using ISC.AI.Vision.Onnx.Video;
using Microsoft.Extensions.Configuration;
using Shouldly;
using Xunit;

namespace ISC.AI.UnitTests.Vision;

/// <summary>
/// Части конвейера распознавания, проверяемые без моделей и ffmpeg (ТО-прог-13): целостность файлов
/// моделей (fail-closed), разрезание MJPEG-потока на кадры, базовая оценка качества, чтение конфигурации.
/// </summary>
public sealed class VisionPipelineUnitTests
{
    private static VisionOptions Options(float minInterocular = 20f, float minScore = 0.9f) =>
        new("d.onnx", "00", "e.onnx", "00", MinInterocularDistance: minInterocular, MinDetectionScoreForQuality: minScore);

    [Fact(DisplayName = "ТИ-004: подменённый файл модели (не тот SHA-256) — явная ошибка с обоими хешами")]
    public void Tampered_model_is_rejected()
    {
        var path = Path.Combine(Path.GetTempPath(), "isc-model-" + Guid.NewGuid().ToString("N") + ".onnx");
        File.WriteAllBytes(path, [1, 2, 3]);
        try
        {
            var error = Should.Throw<InvalidOperationException>(
                () => ModelFileIntegrity.EnsureTrusted(path, "DEADBEEF", "тест"));
            error.Message.ShouldContain("DEADBEEF");
            error.Message.ShouldContain("Целостность");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact(DisplayName = "ТИ-004: без пина SHA-256 модель не используется; отсутствующий файл — понятная ошибка")]
    public void Missing_pin_or_file_is_explicit()
    {
        var missing = Path.Combine(Path.GetTempPath(), "isc-no-such-" + Guid.NewGuid().ToString("N") + ".onnx");
        Should.Throw<FileNotFoundException>(() => ModelFileIntegrity.EnsureTrusted(missing, "00", "тест"));

        var path = Path.Combine(Path.GetTempPath(), "isc-model-" + Guid.NewGuid().ToString("N") + ".onnx");
        File.WriteAllBytes(path, [1]);
        try
        {
            Should.Throw<InvalidOperationException>(() => ModelFileIntegrity.EnsureTrusted(path, "", "тест"))
                .Message.ShouldContain("пин");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact(DisplayName = "MJPEG: два кадра в одном куске, третий — разорванный между кусками")]
    public void Mjpeg_splitter_extracts_frames_across_chunks()
    {
        byte[] Frame(byte payload) => [0xFF, 0xD8, 0xFF, 0xE0, payload, 0x00, 0xFF, 0xD9];
        using var splitter = new MjpegStreamSplitter();
        var first = Frame(1).Concat(Frame(2)).Concat(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 3 }).ToArray();

        var frames = splitter.Push(first);
        frames.Count.ShouldBe(2);
        frames[0].ShouldBe(Frame(1));
        frames[1].ShouldBe(Frame(2));

        var rest = splitter.Push(new byte[] { 0x00, 0xFF, 0xD9 });
        rest.ShouldHaveSingleItem().ShouldBe(Frame(3));
        splitter.Push(new byte[] { 0x00, 0x00 }).ShouldBeEmpty(); // мусор без SOI отбрасывается
    }

    [Fact(DisplayName = "Качество: мелкое лицо непригодно с причиной; крупное и уверенное — пригодно")]
    public void Quality_rejects_small_faces()
    {
        var assessor = new BasicFaceQualityAssessor(Options(minInterocular: 20f));
        var small = new DetectedFace(new BoundingBox(10, 10, 20, 20),
            new FaceLandmarks(new(12, 15), new(20, 15), new(16, 20), new(13, 25), new(19, 25)), 0.95f);
        var large = new DetectedFace(new BoundingBox(100, 100, 200, 200),
            new FaceLandmarks(new(150, 160), new(250, 160), new(200, 210), new(160, 260), new(240, 260)), 0.95f);

        var bad = assessor.Assess(small, 640, 480);
        bad.Acceptable.ShouldBeFalse();
        bad.Reason.ShouldContain("мелкое");

        var good = assessor.Assess(large, 640, 480);
        good.Acceptable.ShouldBeTrue();
        good.Score.ShouldBeGreaterThan(0.9f);
    }

    [Fact(DisplayName = "Качество: лицо, обрезанное границей кадра, непригодно")]
    public void Quality_rejects_faces_cut_by_frame_edge()
    {
        var assessor = new BasicFaceQualityAssessor(Options());
        var cut = new DetectedFace(new BoundingBox(600, 100, 100, 100),
            new FaceLandmarks(new(620, 130), new(680, 130), new(650, 160), new(625, 185), new(675, 185)), 0.95f);

        assessor.Assess(cut, 640, 480).Reason.ShouldContain("обрезано");
    }

    [Fact(DisplayName = "Конфигурация Vision: пути и пины читаются, числа — с дефолтами при мусоре")]
    public void Options_are_read_with_safe_defaults()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new System.Collections.Generic.Dictionary<string, string?>
        {
            ["Vision:Detector:Path"] = "models/yunet.onnx",
            ["Vision:Detector:Sha256"] = "AB",
            ["Vision:Detection:ScoreThreshold"] = "много",
            ["Vision:Quality:MinInterocular"] = "30",
        }).Build();

        var options = VisionOnnxServiceCollectionExtensions.ReadOptions(configuration);

        options.DetectorModelPath.ShouldBe("models/yunet.onnx");
        options.DetectorSha256.ShouldBe("AB");
        options.DetectionScoreThreshold.ShouldBe(0.9f);
        options.MinInterocularDistance.ShouldBe(30f);
        options.EmbedderModelPath.ShouldBe(string.Empty);
    }
}
