using ISC.AI.Abstractions.Modules;
using ISC.AI.Modules.Media.Application;
using ISC.AI.Modules.Media.Data;
using ISC.AI.Modules.Media.Domain.Services;
using ISC.AI.Modules.Media.UI;
using ISC.AI.Vision.Onnx;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;

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
    /// ПОРТЫ ПРОФИЛЯ — что обязан реализовать подключающий профиль (ТС-013): область дел субъекта и
    /// связь носителей/фигурантов с делами (<see cref="ICaseScope"/>), права по ролям
    /// (<see cref="IMediaAdministration"/>) и роли стадий верификации (<see cref="IVerificationPolicy"/>).
    /// Всё остальное пакет закрывает сам (MediaContractTests). Без любой из них контейнер не соберёт
    /// обработчик и приложение не запустится — намеренно.
    /// </summary>
    public static IReadOnlyList<Type> RequiredServices { get; } =
    [
        typeof(ICaseScope),
        typeof(IMediaAdministration),
        typeof(IVerificationPolicy),
    ];

    /// <summary>
    /// Нейтральные службы ядра, которые ОБЯЗАН дать хост (ADR-0018): хранилище файлов, неизменяемый
    /// журнал, политика и контекст доступа, субъект, фоновая очередь. Молчаливая заглушка вместо
    /// правила доступа опаснее остановки — отсутствие любой из них должно ронять старт.
    /// </summary>
    public static IReadOnlyList<Type> RequiredCoreServices { get; } =
    [
        typeof(Abstractions.Storage.IFileStorage),
        typeof(Abstractions.Audit.IAuditWriter),
        typeof(Abstractions.Security.IAccessPolicy),
        typeof(Abstractions.Security.IAccessContextProvider),
        typeof(Abstractions.Security.ISubjectProvider),
        typeof(Abstractions.BackgroundTasks.IBackgroundTaskQueue),
    ];

    /// <summary>
    /// Ключи конфигурации, которые читает пакет: строка подключения и модели — обязательны; параметры
    /// поиска (ТН-008, ТФ-ПЛ-06), раскадровки (ТО-мат-06) и копии пробы в аудите (ТБ-072) — с умолчаниями.
    /// </summary>
    public static IReadOnlyList<string> ConfigurationKeys { get; } =
    [
        "ConnectionStrings:Media",
        MediaPersistenceServiceCollectionExtensions.EfSearchKey,
        "Vision:Detector:Path", "Vision:Detector:Sha256",
        "Vision:Embedder:Path", "Vision:Embedder:Sha256",
        "Vision:Ffmpeg:Folder",
        "Vision:Detection:ScoreThreshold", "Vision:Detection:NmsIou", "Vision:Detection:MaxInputSide",
        "Vision:Quality:MinInterocular", "Vision:Quality:MinDetectionScore",
        MediaSearchOptions.CandidateListSizeKey,
        MediaSearchOptions.MinCandidateListSizeKey,
        MediaSearchOptions.MaxCandidateListSizeKey,
        MediaSearchOptions.MaxCosineDistanceKey,
        MediaSearchOptions.MaxAllowedCosineDistanceKey,
        MediaSearchOptions.SampleFpsKey,
        MediaSearchOptions.ProbeCopyMaxBytesKey,
    ];

    /// <summary>Политика доступа страниц пакета (регистрируется хостом из реестра профиля).</summary>
    public const string ReadPolicy = "media.read";

    /// <summary>
    /// Реестр страниц пакета (ЭС3-02, проект <c>ISC.AI.Modules.Media.UI</c>): пункты меню — поиск по лицу
    /// (ТФ-ПЛ-01) и очередь двойной верификации (ТФ-ВЕР-01). Медиатека дела (<c>/media/cases/{id}</c>),
    /// карточка носителя (<c>/media/assets/{id}</c>), сессия поиска (<c>/media/sessions/{id}</c>) и карточка
    /// решения (<c>/media/verification/{id}</c>) — без пунктов меню: они живут в той же сборке и попадают
    /// в маршрутизацию хоста через <c>ComponentType</c> этих записей (Routes.razor, AddAdditionalAssemblies).
    /// Пары «/media» + «/media/search» намеренно нет: NavLinkMatch.Prefix подсвечивал бы оба пункта.
    /// </summary>
    public static IReadOnlyList<IModule> Modules { get; } =
    [
        new ModuleDescriptor("media-search", "/media/search", "Поиск по лицу",
            Icons.Material.Filled.PersonSearch, typeof(FaceSearch), ReadPolicy, MenuGroup),
        new ModuleDescriptor("media-verification", "/media/verification", "Верификация",
            Icons.Material.Filled.FactCheck, typeof(VerificationQueue), ReadPolicy, MenuGroup),
    ];

    /// <summary>Виджеты оболочки — пакету не нужны (очередь верификации — пунктом меню).</summary>
    public static IReadOnlyList<IShellWidget> ShellWidgets { get; } = [];

    /// <summary>
    /// Конвейер распознавания (ONNX Runtime, ffmpeg) и сценарии пакета (валидаторы, индексатор, настройки
    /// поиска) — вызывается профилем в <c>IProfile.RegisterServices</c>. Обработчики Mediator регистрирует хост.
    /// </summary>
    public static IServiceCollection RegisterServices(IServiceCollection services, IConfiguration configuration) =>
        services.AddVisionOnnx(configuration).AddMediaApplication(configuration);

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
