using ISC.AI.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ISC.AI.Modules.DocFlow.Data;

/// <summary>Регистрация слоя данных модуля документооборота (схема <c>docflow</c>) в контейнере хоста.</summary>
public static class DocFlowPersistenceServiceCollectionExtensions
{
    /// <summary>
    /// Регистрирует <see cref="DocFlowDbContext"/> через фабрику (ТС-008) с провайдером Npgsql.
    /// История миграций — в схеме <c>docflow</c>. Строка подключения — секция
    /// <c>ConnectionStrings:DocFlow</c> (та же БД, что и ядро; при отсутствии — fallback на <c>Core</c>).
    /// </summary>
    public static IServiceCollection AddDocFlowPersistence(
        this IServiceCollection services, IConfiguration configuration)
    {
        // Секрет пароля — отдельно (Database:Password из user-secrets/env), в конфиге лишь несекретная база (Э4-10).
        var connectionString = ConnectionStringResolver.Resolve(configuration, "DocFlow", fallbackName: "Core");

        services.AddDbContextFactory<DocFlowDbContext>(options =>
            options.UseNpgsql(connectionString, npg =>
                    npg.MigrationsHistoryTable("__ef_migrations_history", DocFlowDbContext.Schema))
                .UseSnakeCaseNamingConvention());

        // Порты домена модуля → реализации слоя данных (та же слоистость, что у профиля).
        services.AddScoped<Domain.Services.IDocumentTypeStore, DocumentTypeStore>();
        services.AddScoped<Domain.Services.IDocumentStore, DocumentStore>();
        // Пользователи — реестр ядра core.app_user (вопрос 4 Э4-35); подразделения даёт ПРОФИЛЬ
        // (IDivisionDirectory, вопрос 3) — здесь их реализация не регистрируется намеренно.
        services.AddScoped<Domain.Services.IUserDirectory, UserDirectory>();

        // Системные настройки модуля (§9): значение в БД, меняется без перезапуска. Кеш — SINGLETON
        // (хранилище scoped и своего кеша пережить не может), сам стор — scoped над фабрикой контекста.
        services.AddSingleton<DocFlowSettingsCache>();
        services.AddScoped<Domain.Services.ISystemSettingsStore, SystemSettingsStore>();

        // Часы эксплуатанта (Asia/Bishkek по умолчанию) + фоновая проверка сроков (§4.2: «Просрочено»
        // ставит только система).
        services.AddSingleton<Domain.Services.IDocFlowClock, DocFlowClock>();

        // Сама проверка сроков — отдельным scoped-сервисом: её зовёт и фоновая задача (создавая
        // свой scope на тик), и ручной запуск администратором. Повторный вызов безопасен —
        // перевод в «Просрочено» идемпотентен, уведомления отсекает дедупликация.
        services.AddScoped<Domain.Services.IDeadlineChecker, DeadlineChecker>();
        services.AddHostedService<DeadlineCheckerJob>();

        // Индексация документов в корпус ядра (этап 7 Э4-35): вызывается очередью фоновых задач.
        services.AddScoped<Domain.Services.IDocumentIndexer, DocFlowDocumentIndexer>();

        // Файловое хранилище модуля (этап 4.2, §3.3): локальная ФС контура, путь — DocFlow:Storage:BasePath.
        services.AddSingleton<Domain.Services.IDocFlowFileStorage, LocalDocFlowFileStorage>();

        // Просмотр файлов без скачивания (этап 4.3, §3.3): резолвинг для эндпоинта раздачи.
        services.AddScoped<Domain.Services.IDocumentFileAccess, DocumentFileAccessResolver>();

        // Комментарии к документам (этап 2.2, §4.8): обсуждение с ответами, упоминаниями и файлами.
        services.AddScoped<Domain.Services.ICommentStore, CommentStore>();

        // Уведомления (этап 2.2b, разд. 5): сроки, статусы, упоминания. Только в интерфейсе, без email.
        services.AddScoped<Domain.Services.INotificationStore, NotificationStore>();

        // Данные отчётов (этап 5, разд. 6): разграничение — В ЗАПРОСЕ, отчёт это массовая выгрузка.
        services.AddScoped<Domain.Services.IReportDataSource, ReportDataSource>();

        // Данные дашборда модуля: то же разграничение — агрегат утекает числом, а не текстом.
        services.AddScoped<Domain.Services.IDashboardDataSource, DashboardDataSource>();

        // Ответ модуля профилю на вопрос «сколько на подразделении документов и поручений»:
        // справочник ведёт профиль, поручения живут здесь, соединить схемы одним запросом нельзя.
        services.AddScoped<Domain.Services.IDivisionUsage, DivisionUsageQuery>();

        // Разрешение слабых ссылок по RegNumber (ТО-инф-06) для чужих модулей — напр. архив
        // проверок профиля обогащает «справку-проверку» нарушения; решётка доступа — в запросе.
        services.AddScoped<Domain.Services.IDocumentLookup, DocumentLookup>();

        // Рендереры отчётов — все три сразу; сценарий выбирает нужный по ReportFormat.
        // Шрифт для PDF берётся из системы по настраиваемому пути: вшить его в сборку нельзя
        // из-за лицензий, скачать — из-за изолированного контура (см. PdfReportFontOptions).
        services.AddSingleton(new Reports.PdfReportFontOptions(
            configuration["DocFlow:Reports:PdfFont:Regular"],
            configuration["DocFlow:Reports:PdfFont:Bold"]));

        services.AddSingleton<Domain.Services.IReportRenderer, Reports.ExcelReportRenderer>();
        services.AddSingleton<Domain.Services.IReportRenderer, Reports.WordReportRenderer>();
        services.AddSingleton<Domain.Services.IReportRenderer, Reports.PdfReportRenderer>();

        return services;
    }
}
