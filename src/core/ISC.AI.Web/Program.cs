using System.Globalization;
using System.Reflection;
using ISC.AI.Abstractions.Profiles;
using ISC.AI.Abstractions.Security;
using ISC.AI.AI.Audit;
using ISC.AI.AI.BackgroundTasks;
using ISC.AI.AI.Grounding;
using ISC.AI.AI.Models;
using ISC.AI.AI.Rag;
using ISC.AI.AI.Retrieval;
using ISC.AI.Documents;
using ISC.AI.Ingestion;
using ISC.AI.Persistence;
using ISC.AI.Web.Common.Behaviors;
using ISC.AI.Web.Security;
using ISC.AI.Profile.Inspector;
using ISC.AI.Web.Components;
using Mediator;
using MudBlazor.Services;
using Serilog;
using Serilog.Events;

// Bootstrap-логгер: ловит ошибки до построения хоста (Serilog).
Log.Logger = new LoggerConfiguration()
    .WriteTo.Console(formatProvider: CultureInfo.InvariantCulture)
    .CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    // Логирование — Serilog (в изолированном контуре пишем в консоль/журнал, без внешних приёмников).
    builder.Services.AddSerilog((_, cfg) => cfg
        .MinimumLevel.Information()
        .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
        .Enrich.FromLogContext()
        .WriteTo.Console(formatProvider: CultureInfo.InvariantCulture));

    // Blazor Server + MudBlazor.
    builder.Services.AddRazorComponents()
        .AddInteractiveServerComponents();
    builder.Services.AddMudServices();

    // CQRS-lite: Mediator (source-генератор — в этом хосте). Обработчики — Scoped: они тянут
    // scoped-сервисы RAG (retriever/оркестратор работают через IDbContextFactory на операцию).
    builder.Services.AddMediator(options => options.ServiceLifetime = ServiceLifetime.Scoped);

    // Сквозной конвейер Mediator (порядок = порядок регистрации): обработка ошибок (внешняя) →
    // логирование → валидация → хендлер. ValidationBehavior бросает, ExceptionHandling превращает
    // исключения в неуспешный ResponseDto (хендлеры — без ручных проверок).
    builder.Services.AddTransient(typeof(IPipelineBehavior<,>), typeof(ExceptionHandlingBehavior<,>));
    // Сквозной аудит (Э4-11, ТБ-030, инвариант №4): каждый аудируемый сценарий (IAuditableRequest) пишется
    // в неизменяемый журнал — аудит нельзя «забыть» в хендлере. Внешним слоем после обработки ошибок.
    builder.Services.AddTransient(typeof(IPipelineBehavior<,>), typeof(AuditBehavior<,>));
    builder.Services.AddTransient(typeof(IPipelineBehavior<,>), typeof(LoggingBehavior<,>));
    builder.Services.AddTransient(typeof(IPipelineBehavior<,>), typeof(ValidationBehavior<,>));

    // Грунтовка как СТРАХОВКА конвейера (Э4-19, §5.3.1.1, ТБ-041): грунтующие сценарии (IGroundedScenario)
    // не могут вернуть успех без вердикта грунтовки; непроверенные ссылки помечаются. Поведение — в ядре.
    builder.Services.AddTransient(typeof(IPipelineBehavior<,>), typeof(GroundingBehavior<,>));

    // Слой данных ядра: CoreDbContext (схема core) через фабрику (ТС-008, ТО-инф-01).
    builder.Services.AddCorePersistence(builder.Configuration);

    // Локальные модели за IChatClient/IEmbeddingGenerator, keyed по роли (ADR-0004, ТО-прог-02/03, ТБ-044).
    builder.Services.AddCoreAiModels(builder.Configuration);

    // RAG-извлечение с обязательным фильтром доступа на стороне БД (ADR-0007, ТБ-020, GATE-1).
    builder.Services.AddCoreRetrieval();

    // Грунтовка: ссылки только из извлечённых фрагментов; «по памяти» запрещено (ТБ-040, GATE-2).
    builder.Services.AddCoreGrounding();

    // Движок документов: извлечение текста из файлов (.txt/.docx; OCR — далее) для загрузки корпуса (Э4-01).
    builder.Services.AddCoreDocuments();

    // Загрузка в корпус: fail-closed (без грифа/подразделения — отказ), идемпотентно (ТБ-024, ТНД-002).
    builder.Services.AddCoreIngestion();

    // RAG-оркестратор: запрос → retriever (фильтр доступа) → промпт+модель → грунтовка → ответ (ТО-мат-01).
    builder.Services.AddCoreRag();

    // Очередь фоновых ИИ-задач (Э4-20, §5.1.3): интеллектуальные операции — асинхронно, со статусом и
    // управляемой деградацией; при старте — восстановление осиротевших задач. Store — из AddCorePersistence.
    builder.Services.AddCoreBackgroundTasks();

    // DEV-заглушка контекста доступа (заменяется внешним SSO на Э3-08). Только в Development.
    if (builder.Environment.IsDevelopment())
    {
        builder.Services.AddScoped<IAccessContextProvider, DevAccessContextProvider>();
    }

    // --- Точка композиции профиля (ТО-прог-05/06). Только хост знает о конкретном профиле. ---
    var profile = new InspectorProfile();
    builder.Services.AddSingleton<IProfile>(profile);
    profile.RegisterServices(builder.Services, builder.Configuration);
    profile.RegisterDataContexts(builder.Services, builder.Configuration);
    foreach (var contributor in profile.ModelContributors)
    {
        contributor.Register(builder.Services, builder.Configuration);
    }

    // Авторизация. ВНИМАНИЕ: аутентификация и разграничение по допуску (ТБ-010..016)
    // подключаются вместе со слоем идентификации на этапе Э3; до этого фолбэк-политика
    // «только аутентифицированные» не включается, чтобы каркас был запускаем в разработке.
    builder.Services.AddAuthorization();

    var app = builder.Build();

    if (!app.Environment.IsDevelopment())
    {
        app.UseExceptionHandler("/Error", createScopeForErrors: true);
        app.UseHsts();
    }

    app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
    app.UseHttpsRedirection();
    app.UseSerilogRequestLogging();
    app.UseAntiforgery();
    app.UseAuthorization();
    app.MapStaticAssets();

    // Сборки, содержащие страницы модулей профиля, — для маршрутизации хоста.
    Assembly[] moduleAssemblies = [.. profile.Modules.Select(m => m.ComponentType.Assembly).Distinct()];
    app.MapRazorComponents<App>()
        .AddInteractiveServerRenderMode()
        .AddAdditionalAssemblies(moduleAssemblies);

    Log.Information("Хост ISC.AI запущен с профилем {ProfileId} ({ProfileName})", profile.Id, profile.DisplayName);
    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Хост ISC.AI завершился аварийно при запуске");
}
finally
{
    Log.CloseAndFlush();
}
