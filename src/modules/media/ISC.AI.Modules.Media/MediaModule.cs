using ISC.AI.Abstractions.Modules;
using ISC.AI.Modules.Media.Data;
using ISC.AI.Vision.Onnx;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ISC.AI.Modules.Media;

/// <summary>
/// Манифест пакета модулей «Медиа» (ADR-0017, ДОК-13 §5): хранение фото/видео, распознавание и
/// поиск лиц — переиспользуемая вертикаль, которую ПРОФИЛЬ («Следствие», ЭС3) включает в свой
/// реестр. Пакет НЕ зависит ни от профиля, ни от хоста (DependencyRulesTests).
/// </summary>
/// <remarks>
/// Порядок вызова со стороны профиля повторяет порядок композиции хоста (ТО-прог-05):
/// <see cref="RegisterServices"/> → <see cref="RegisterDataContexts"/> → <see cref="MapEndpoints"/>.
/// Инспектор этот пакет НЕ подключает — его старт и данные не меняются.
/// </remarks>
public static class MediaModule
{
    /// <summary>Секция меню, под которой профиль группирует страницы пакета.</summary>
    public const string MenuGroup = "Медиа";

    /// <summary>
    /// Нейтральные службы ядра, которые ОБЯЗАН дать хост/профиль (ADR-0018): без любой из них
    /// контейнер не соберёт обработчик и приложение не запустится — намеренно (молчаливая заглушка
    /// вместо правила доступа опаснее остановки).
    /// </summary>
    public static IReadOnlyList<Type> RequiredServices { get; } =
    [
        typeof(Abstractions.Storage.IFileStorage),
        typeof(Abstractions.Audit.IAuditWriter),
        typeof(Abstractions.Security.IAccessPolicy),
        typeof(Abstractions.Security.IAccessContextProvider),
    ];

    /// <summary>Ключи конфигурации, которые читает пакет (строка подключения и модели — обязательны).</summary>
    public static IReadOnlyList<string> ConfigurationKeys { get; } =
    [
        "ConnectionStrings:Media",
        MediaPersistenceServiceCollectionExtensions.EfSearchKey,
        "Vision:Detector:Path", "Vision:Detector:Sha256",
        "Vision:Embedder:Path", "Vision:Embedder:Sha256",
        "Vision:Ffmpeg:Folder",
        "Vision:Detection:ScoreThreshold", "Vision:Detection:NmsIou", "Vision:Detection:MaxInputSide",
        "Vision:Quality:MinInterocular", "Vision:Quality:MinDetectionScore",
    ];

    /// <summary>Политика доступа страниц пакета (регистрируется хостом из реестра профиля).</summary>
    public const string ReadPolicy = "media.read";

    /// <summary>Реестр страниц пакета — пока пуст: UI появится на этапе ЭС4 отдельным проектом .UI.</summary>
    public static IReadOnlyList<IModule> Modules { get; } = [];

    /// <summary>Виджеты оболочки — пока нет.</summary>
    public static IReadOnlyList<IShellWidget> ShellWidgets { get; } = [];

    /// <summary>Конвейер распознавания (ONNX Runtime, ffmpeg) — вызывается профилем в <c>IProfile.RegisterServices</c>.</summary>
    public static IServiceCollection RegisterServices(IServiceCollection services, IConfiguration configuration) =>
        services.AddVisionOnnx(configuration);

    /// <summary>Контекст данных и порты хранения/поиска — вызывается профилем в <c>IProfile.RegisterDataContexts</c>.</summary>
    public static IServiceCollection RegisterDataContexts(IServiceCollection services, IConfiguration configuration) =>
        services.AddMediaPersistence(configuration);

    /// <summary>Сырые HTTP-эндпоинты пакета: раздача файлов носителей и вырезок (ТБ-073).</summary>
    public static IEndpointRouteBuilder MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapMediaFileEndpoints();
        return endpoints;
    }
}
