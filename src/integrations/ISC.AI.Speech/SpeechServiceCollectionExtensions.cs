using System.Globalization;
using ISC.AI.Modules.Media.Domain.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ISC.AI.Speech;

/// <summary>
/// Регистрация распознавания речи (ADR-0026) — вызывается манифестом пакета «Медиа», не ядром (ТС-011).
/// </summary>
public static class SpeechServiceCollectionExtensions
{
    /// <summary>
    /// Читает секцию <c>Speech</c> и регистрирует <see cref="IAudioTranscriber"/>: рабочую реализацию
    /// (<see cref="SpeechWorkerTranscriber"/>), если заданы путь к процессу-распознавателю и пути с пинами
    /// всех трёх файлов модели; иначе — явный отказ (<see cref="UnconfiguredAudioTranscriber"/>) с перечнем
    /// недостающих ключей. Регистрация диск не трогает: наличие файлов и пины проверяются при каждом
    /// запуске расшифровки (ТИ-004). В обоих случаях регистрируется уборка остатков прошлых прогонов при
    /// старте хоста (<see cref="SpeechWorkDirSweeper"/>).
    /// </summary>
    public static IServiceCollection AddSpeechTranscription(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var options = ReadOptions(configuration);
        services.AddSingleton(options);

        // Уборка — и когда расшифровка сейчас НЕ настроена: остатки (копии звука из материалов дела) могли
        // остаться от прошлого запуска, когда она была настроена, и должны уйти с диска (ТБ-064).
        services.AddHostedService<SpeechWorkDirSweeper>();

        var missing = options.MissingKeys();
        if (missing.Count > 0)
        {
            services.AddSingleton<IAudioTranscriber>(new UnconfiguredAudioTranscriber(missing));
        }
        else
        {
            services.AddSingleton<SpeechWorkerTranscriber>();
            services.AddSingleton<IAudioTranscriber>(sp => sp.GetRequiredService<SpeechWorkerTranscriber>());
        }

        return services;
    }

    /// <summary>
    /// Параметры из конфигурации. Каталог ffmpeg: <c>Speech:Ffmpeg:Folder</c>, пусто — <c>Vision:Ffmpeg:Folder</c>
    /// (одна поставка ffmpeg на оба конвейера). Числовые — с безопасными умолчаниями при отсутствии или мусоре
    /// и с ограничением диапазона (длина куска — не больше предела модели, длительность записи — не больше
    /// жёсткого предела детектора речи, <see cref="SpeechOptions.MaxAllowedDurationHours"/>).
    /// </summary>
    public static SpeechOptions ReadOptions(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var ffmpeg = configuration[SpeechConfigurationKeys.FfmpegFolder];
        if (string.IsNullOrWhiteSpace(ffmpeg))
        {
            ffmpeg = configuration[SpeechConfigurationKeys.VisionFfmpegFolder];
        }

        return new SpeechOptions(
            WorkerPath: Text(configuration, SpeechConfigurationKeys.WorkerPath),
            ModelPath: Text(configuration, SpeechConfigurationKeys.ModelPath),
            ModelSha256: Text(configuration, SpeechConfigurationKeys.ModelSha256),
            TokensPath: Text(configuration, SpeechConfigurationKeys.TokensPath),
            TokensSha256: Text(configuration, SpeechConfigurationKeys.TokensSha256),
            VadPath: Text(configuration, SpeechConfigurationKeys.VadPath),
            VadSha256: Text(configuration, SpeechConfigurationKeys.VadSha256),
            FfmpegFolder: string.IsNullOrWhiteSpace(ffmpeg) ? null : ffmpeg.Trim(),
            Threads: int.TryParse(configuration[SpeechConfigurationKeys.Threads], NumberStyles.Integer, CultureInfo.InvariantCulture, out var threads) && threads > 0
                ? Math.Min(threads, 64)
                : SpeechOptions.DefaultThreads,
            MaxSegmentSeconds: Positive(configuration, SpeechConfigurationKeys.MaxSegmentSeconds, SpeechOptions.DefaultMaxSegmentSeconds,
                SpeechOptions.MinAllowedSegmentSeconds, SpeechOptions.MaxAllowedSegmentSeconds),
            ModelName: string.IsNullOrWhiteSpace(configuration[SpeechConfigurationKeys.ModelName])
                ? SpeechOptions.DefaultModelName
                : configuration[SpeechConfigurationKeys.ModelName]!.Trim(),
            MaxDurationHours: Positive(configuration, SpeechConfigurationKeys.MaxDurationHours, SpeechOptions.DefaultMaxDurationHours,
                SpeechOptions.MinAllowedDurationHours, SpeechOptions.MaxAllowedDurationHours),
            TimeoutFactor: Positive(configuration, SpeechConfigurationKeys.TimeoutFactor, SpeechOptions.DefaultTimeoutFactor,
                SpeechOptions.MinAllowedTimeoutFactor, SpeechOptions.MaxAllowedTimeoutFactor));
    }

    private static string Text(IConfiguration configuration, string key) => configuration[key]?.Trim() ?? string.Empty;

    // Положительное число в инвариантной записи (точка); отсутствие, мусор, ноль и отрицательное — умолчание,
    // вне диапазона — ближайшая граница.
    private static double Positive(IConfiguration configuration, string key, double fallback, double min, double max) =>
        double.TryParse(configuration[key], NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
        && double.IsFinite(value) && value > 0
            ? Math.Clamp(value, min, max)
            : fallback;
}
