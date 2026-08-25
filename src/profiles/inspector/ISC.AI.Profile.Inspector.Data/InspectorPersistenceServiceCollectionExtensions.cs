using ISC.AI.Abstractions.Security;
using ISC.AI.Persistence;
using ISC.AI.Profile.Inspector.Domain.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ISC.AI.Profile.Inspector.Data;

/// <summary>Регистрация доменного слоя данных профиля (схема <c>inspector</c>) в контейнере хоста.</summary>
public static class InspectorPersistenceServiceCollectionExtensions
{
    /// <summary>
    /// Регистрирует <see cref="InspectorDbContext"/> через фабрику (ТС-008) с провайдером Npgsql.
    /// История миграций — в схеме <c>inspector</c>. Строка подключения — секция
    /// <c>ConnectionStrings:Inspector</c> (та же БД, что и ядро; при отсутствии — fallback на <c>Core</c>).
    /// </summary>
    public static IServiceCollection AddInspectorPersistence(
        this IServiceCollection services, IConfiguration configuration)
    {
        // Секрет пароля — отдельно (Database:Password из user-secrets/env), в конфиге лишь несекретная база (Э4-10).
        var connectionString = ConnectionStringResolver.Resolve(configuration, "Inspector", fallbackName: "Core");

        services.AddDbContextFactory<InspectorDbContext>(options =>
            options.UseNpgsql(connectionString, npg =>
                    npg.MigrationsHistoryTable("__ef_migrations_history", InspectorDbContext.Schema))
                .UseSnakeCaseNamingConvention());

        // Материализация статуса редакции НПА → флаг годности ядра по связкам (Э4-02, ADR-0013).
        services.AddScoped<IRevisionStatusMaterializer, RevisionStatusMaterializer>();

        // Картотека НПА (ТФ-НПА-02): реестр норм, редакции, привязка документов корпуса — то,
        // что наполняет связки ChunkRevisionLink и делает материализацию статуса рабочей.
        services.AddScoped<INormRegistryStore, NormRegistryStore>();

        // Каталог корпуса НПА (ТФ-НПА-01/02): реестр документов корпуса со статусом действия + карточка
        // с текстом — вид «как в ЦБД Минюста»; дополняет семантический поиск.
        services.AddScoped<ICorpusCatalog, CorpusCatalog>();

        // Автонаполнение картотеки из корпуса (метаданные ЦБД): нормы/редакции/связки без ручного ввода.
        services.AddScoped<INpaRegistrySynchronizer, NpaRegistrySynchronizer>();

        // Учёт нарушений (Э5-01): реестр/карточка, классификатор видов, сигналы риска и сводка
        // дашборда. Балл риска — детерминированный код (Приложение §2), не ИИ.
        services.AddScoped<IViolationStore, ViolationStore>();
        services.AddScoped<IViolationCategoryStore, ViolationCategoryStore>();
        services.AddScoped<IRiskDataSource, RiskDataSource>();

        // Архив проверок (§5.2.4): группы «справка × подразделение» из учёта нарушений; ссылки
        // на справки разрешаются через порт документооборота ОТ ИМЕНИ субъекта (решётка — там).
        services.AddScoped<IInspectionArchiveStore, InspectionArchiveStore>();
        services.AddScoped<IInspectionDocumentResolver, InspectionDocumentResolver>();

        // Автопросрочка устранения (ТФ-МОН-01): истёкший контрольный срок помечает система.
        services.AddHostedService<RemediationDeadlineJob>();

        // Реестр методик (§5.2.9, Ц-03): сохранённые методические документы; выдача — по допуску.
        services.AddScoped<IMethodRegistryStore, MethodRegistryStore>();

        // Совещания (§5.2.7): протокол читается из документооборота от имени субъекта — для справки
        // об исполнении; имена подразделений — из справочника профиля.
        services.AddScoped<IMeetingProtocolReader, MeetingProtocolReader>();

        // Профиль отдаёт модулю документооборота свой справочник подразделений (вопрос 3 Э4-35):
        // словарь id един с решёткой доступа, справочник ведёт профиль.
        services.AddScoped<ISC.AI.Modules.DocFlow.Domain.Services.IDivisionDirectory, DocFlowDivisionDirectory>();

        // Профиль отдаёт модулю ответ на вопрос «кто вправе вести его настройки» (§9): роль знает
        // только профиль. Без этой регистрации право не имеет никто (fail-closed).
        services.AddScoped<ISC.AI.Modules.DocFlow.Domain.Services.IDocFlowAdministration, DocFlowAdministration>();

        // Кого предлагать в инспекторы и исполнители (§3.2/§4.1): ответ зависит от роли и допуска —
        // и то и другое вне модуля.
        services.AddScoped<
            ISC.AI.Modules.DocFlow.Domain.Services.IAssignmentCandidateDirectory,
            AssignmentCandidateDirectory>();

        // Ведение справочника подразделений (§4.2) — страница «Территориальные».
        services.AddScoped<IDivisionAdminStore, DivisionAdminStore>();

        // Роли пользователей (§2.1 ТЗ СКИД, этап 6 Э4-35) — построчный доступ к докфлоу-документам.
        services.AddScoped<IUserRoleStore, UserRoleStore>();

        // Сверка допусков со справочником подразделений на старте: словарь номеров обязан быть общим
        // у решётки ядра и справочника профиля, но ничем не проверяется (см. сам класс).
        services.AddHostedService<ClearanceDivisionConsistencyCheck>();

        // Переопределяет AllowAllAccessPolicy ядра (AddCoreRetrieval регистрируется РАНЬШЕ — Program.cs)
        // тем же приёмом, что и ICitationExtractor/ICitationNormalizer: явная замена дефолта повторной
        // регистрацией, не вторая параллельная. Singleton — как у дефолта; IDbContextFactory сам по себе
        // потокобезопасен, конкретный DbContext создаётся заново на каждый вызов BuildFilter.
        services.AddSingleton<IAccessPolicy, InspectorAccessPolicy>();

        return services;
    }
}
