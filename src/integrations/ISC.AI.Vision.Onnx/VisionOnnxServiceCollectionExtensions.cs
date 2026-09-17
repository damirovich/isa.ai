using System.Globalization;
using ISC.AI.Modules.Media.Domain.Services;
using ISC.AI.Vision.Onnx.Detection;
using ISC.AI.Vision.Onnx.Embedding;
using ISC.AI.Vision.Onnx.Quality;
using ISC.AI.Vision.Onnx.Video;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ISC.AI.Vision.Onnx;

/// <summary>Регистрация реализаций портов «Медиа» на ONNX Runtime/ffmpeg (вызывается модулем или профилем, не ядром).</summary>
public static class VisionOnnxServiceCollectionExtensions
{
    /// <summary>
    /// Читает секцию <c>Vision</c> и регистрирует детектор, векторизатор, оценку качества и раскадровку.
    /// Модели загружаются лениво при первом обращении — регистрация не трогает диск; ошибки целостности
    /// всплывают явно на первом использовании (ТИ-004).
    /// </summary>
    public static IServiceCollection AddVisionOnnx(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton(ReadOptions(configuration));
        services.AddSingleton<YuNetFaceDetector>();
        services.AddSingleton<IFaceDetector>(sp => sp.GetRequiredService<YuNetFaceDetector>());
        services.AddSingleton<SFaceEmbedder>();
        services.AddSingleton<IFaceEmbedder>(sp => sp.GetRequiredService<SFaceEmbedder>());
        services.AddSingleton<IFaceQualityAssessor, BasicFaceQualityAssessor>();
        services.AddSingleton<IFrameExtractor, FfmpegFrameExtractor>();
        return services;
    }

    /// <summary>Параметры из конфигурации; числовые — с безопасными дефолтами при отсутствии/мусоре.</summary>
    public static VisionOptions ReadOptions(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return new VisionOptions(
            DetectorModelPath: configuration["Vision:Detector:Path"] ?? string.Empty,
            DetectorSha256: configuration["Vision:Detector:Sha256"] ?? string.Empty,
            EmbedderModelPath: configuration["Vision:Embedder:Path"] ?? string.Empty,
            EmbedderSha256: configuration["Vision:Embedder:Sha256"] ?? string.Empty,
            FfmpegFolder: configuration["Vision:Ffmpeg:Folder"],
            DetectionScoreThreshold: ReadFloat(configuration, "Vision:Detection:ScoreThreshold", 0.9f),
            NmsIouThreshold: ReadFloat(configuration, "Vision:Detection:NmsIou", 0.3f),
            MaxInputSide: ReadInt(configuration, "Vision:Detection:MaxInputSide", 640),
            MinInterocularDistance: ReadFloat(configuration, "Vision:Quality:MinInterocular", 20f),
            MinDetectionScoreForQuality: ReadFloat(configuration, "Vision:Quality:MinDetectionScore", 0.9f));
    }

    private static float ReadFloat(IConfiguration configuration, string key, float fallback) =>
        float.TryParse(configuration[key], NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && value > 0
            ? value
            : fallback;

    private static int ReadInt(IConfiguration configuration, string key, int fallback) =>
        int.TryParse(configuration[key], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value > 0
            ? value
            : fallback;
}
