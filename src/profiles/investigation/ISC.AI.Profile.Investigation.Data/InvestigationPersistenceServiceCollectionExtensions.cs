using Microsoft.Extensions.DependencyInjection.Extensions;
using ISC.AI.Abstractions.Security;
using ISC.AI.Persistence;
using ISC.AI.Profile.Investigation.Domain.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ISC.AI.Profile.Investigation.Data;

/// <summary>Регистрация слоя данных профиля «Следствие» (схема <c>investigation</c>) в контейнере хоста.</summary>
public static class InvestigationPersistenceServiceCollectionExtensions
{
    /// <summary>
    /// Регистрирует <see cref="InvestigationDbContext"/> через фабрику (ТС-008) с провайдером Npgsql,
    /// хранилища профиля и реализации портов пакетов «Документооборот» и «Медиа». История миграций — в
    /// схеме <c>investigation</c>. Строка подключения — <c>ConnectionStrings:Investigation</c> (та же БД,
    /// что и ядро; при отсутствии — fallback на <c>Core</c>). Вызывать ПОСЛЕ регистрации ядра:
    /// <see cref="IAccessPolicy"/> здесь переопределяет <c>AllowAllAccessPolicy</c>.
    /// </summary>
    public static IServiceCollection AddInvestigationPersistence(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // Секрет пароля — отдельно (Database:Password из user-secrets/env), в конфиге лишь несекретная база (Э4-10).
        var connectionString = ConnectionStringResolver.Resolve(configuration, "Investigation", fallbackName: "Core");

        services.AddDbContextFactory<InvestigationDbContext>(options =>
            options.UseNpgsql(connectionString, npg =>
                    npg.MigrationsHistoryTable("__ef_migrations_history", InvestigationDbContext.Schema))
                .UseSnakeCaseNamingConvention());

        // Эта база — в резервную копию (ADR-0063): копируется то, чем пользуется поставка, а не все строки из настроек.
        services.AddSingleton(new ISC.AI.Abstractions.Backup.BackupDatabase("investigation", connectionString));

        // Роли (ТП-004) и справочник подразделений (ТФ-АДМ-01) — основа всех остальных проверок.
        services.AddScoped<IUserRoleStore, UserRoleStore>();
        services.AddScoped<IDivisionAdminStore, DivisionAdminStore>();

        // Справочники профиля (ТФ-АДМ-07): инициаторы заданий, звания, должности, типы связей, категории.
        services.AddScoped<IReferenceStore, ReferenceStore>();

        // Дела и фигуранты (ТФ-ДЕЛ-01..03, ТФ-ПЕР-01/02): решётка ядра + CaseAccessRule на стороне БД.
        services.AddScoped<ICaseStore, CaseStore>();
        services.AddScoped<IPersonStore, PersonStore>();

        // Адреса и автотранспорт фигурантов (ТФ-ПЕР-06) — та же решётка через PersonAccess.
        services.AddScoped<IPersonRequisiteStore, PersonRequisiteStore>();
        services.AddScoped<ILinkEventStore, LinkEventStore>();
        services.AddScoped<ISearchHistoryStore, SearchHistoryStore>();

        // Пересечения между делами (ТФ-ПЕР-07, ТБ-084, ADR-0029): floor допуска без сужения по роли.
        services.AddScoped<IIntersectionStore, IntersectionStore>();

        // Сводки и справки по бланку (ТФ-ДДЛ-04/05, ADR-0031): окно редактирования считается по часам
        // TimeProvider (подменяются в тестах), выгрузка .docx — отрисовщиком профиля.
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<ICaseReportStore, CaseReportStore>();

        // Отчётные формы [АИС 6] (ADR-0061): журнал суточной активности и статистика карточек объектов — под правилом дел.
        services.AddScoped<IActivityReportStore, ActivityReportStore>();

        // Перенос баз Access (ADR-0043): чтение файла базы драйвером ACE только на чтение и учёт перенесённых
        // записей для повтора без дублей.
        services.AddSingleton<IAccessDatabaseReader, OleDbAccessDatabaseReader>();
        services.AddScoped<IAccessImportRecordStore, AccessImportRecordStore>();

        // Документы дела (ТФ-ДЕЛ-02): привязки к документам документооборота по значению.
        services.AddScoped<ICaseDocumentStore, CaseDocumentStore>();
        services.AddSingleton<ICaseReportRenderer, Reports.CaseReportDocxRenderer>();

        // Справки по делу и фигуранту в .docx (ADR-0053): таблицы фактов, изложение, гриф и реквизиты ТБ-033.
        services.AddSingleton<IBriefRenderer, Reports.BriefDocxRenderer>();

        // Чат по материалам дел (ADR-0051/0052): дела-области чата по полной решётке (переписку ведёт пакет «Чат» в таблицах
        // ядра); сведения дела и сводки — в корпус ядра по запросу и фоновым дозаполнением после старта.
        services.AddScoped<ICaseChatScopes, CaseChatScopes>();
        services.AddScoped<ICaseCorpusSync, CaseCorpusSync>();
        services.AddHostedService<CaseCorpusStartup>();

        // Дозаполнение нормализованных реквизитов прежних фигурантов при старте (ТО-мат-11).
        services.AddHostedService<InvestigationStartupMaintenance>();

        // Порты пакета «Документооборот» (ADR-0017): справочник подразделений, право настройки,
        // кандидаты в ответственные/исполнители. Без реализаций модуль fail-closed.
        services.AddScoped<ISC.AI.Modules.DocFlow.Domain.Services.IDivisionDirectory, InvestigationDivisionDirectory>();
        services.AddScoped<ISC.AI.Modules.DocFlow.Domain.Services.IDocFlowAdministration, InvestigationDocFlowAdministration>();

        // Разделы меню по роли (ТП-004): хост прячет то, где роль ничего не может; права проверяет сервер (ТБ-012).
        services.AddScoped<ISC.AI.Abstractions.Modules.IModuleVisibility, InvestigationModuleVisibility>();
        services.AddScoped<ISC.AI.Modules.DocFlow.Domain.Services.IAssignmentCandidateDirectory, InvestigationAssignmentCandidateDirectory>();

        // Порты пакета «Медиа»: область дел субъекта (ТБ-071) и доступность носителя по прямому
        // идентификатору (CaseScope: хранилища + фабрика контекста + политика + роли — всё зарегистрировано
        // выше), права по роли (ТП-004), стадии верификации (ТБ-073). Без ICaseScope приложение стартовать
        // не должно (ТС-013).
        services.AddScoped<ISC.AI.Modules.Media.Domain.Services.ICaseScope, CaseScope>();
        services.AddScoped<ISC.AI.Modules.Media.Domain.Services.IMediaAdministration, MediaAdministration>();
        services.AddScoped<ISC.AI.Modules.Media.Domain.Services.IVerificationPolicy, VerificationPolicy>();

        // Порты пакета «Администрирование платформы» (ADR-0023): право вести учётные записи и допуски
        // и право читать журнал (ТП-004: Администратор и Офицер ИБ), наименования подразделений для
        // экрана допусков, роли для колонки и фильтра учётных записей. Без них модуль fail-closed.
        // Стартовую сверку допусков со справочником подразделений ведёт сам пакет (AddAdminApplication).
        services.AddScoped<ISC.AI.Modules.Admin.Domain.Services.IPlatformAdministration, InvestigationPlatformAdministration>();
        services.AddScoped<ISC.AI.Modules.Admin.Domain.Services.IDivisionCatalog, InvestigationDivisionCatalog>();
        services.AddScoped<ISC.AI.Modules.Admin.Domain.Services.IUserRoleCatalog, InvestigationUserRoleCatalog>();

        // Переопределяет AllowAllAccessPolicy ядра (ядро регистрируется РАНЬШЕ — Program.cs) явной
        // повторной регистрацией. Singleton — как у дефолта; IDbContextFactory потокобезопасен,
        // конкретный DbContext создаётся заново на каждый вызов BuildFilter.
        services.AddSingleton<IAccessPolicy, InvestigationAccessPolicy>();

        return services;
    }
}
