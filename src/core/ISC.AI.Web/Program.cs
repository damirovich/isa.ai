using System.Globalization;
using System.Reflection;
using ISC.AI.Abstractions.Profiles;
using ISC.AI.AI.Models;
using ISC.AI.AI.Retrieval;
using ISC.AI.Persistence;
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

    // CQRS-lite: Mediator (source-генератор — в этом хосте).
    builder.Services.AddMediator();

    // Слой данных ядра: CoreDbContext (схема core) через фабрику (ТС-008, ТО-инф-01).
    builder.Services.AddCorePersistence(builder.Configuration);

    // Локальные модели за IChatClient/IEmbeddingGenerator, keyed по роли (ADR-0004, ТО-прог-02/03, ТБ-044).
    builder.Services.AddCoreAiModels(builder.Configuration);

    // RAG-извлечение с обязательным фильтром доступа на стороне БД (ADR-0007, ТБ-020, GATE-1).
    builder.Services.AddCoreRetrieval();

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
