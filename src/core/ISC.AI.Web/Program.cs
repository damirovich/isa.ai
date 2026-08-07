using System.Globalization;
using System.Net;
using System.Reflection;
using System.Threading.RateLimiting;
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
using ISC.AI.Persistence.Security;
using ISC.AI.Web.Common.Behaviors;
using ISC.AI.Web.Security;
using ISC.AI.Profile.Inspector;
using ISC.AI.Web.Components;
using Mediator;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
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
        // DetailedErrors ТОЛЬКО в Development: в браузер уходит текст исключения со стеком, а это
        // выдача внутреннего устройства системы наружу (ТБ-010). В контуре ошибку ищут по журналу.
        .AddInteractiveServerComponents(options =>
            options.DetailedErrors = builder.Environment.IsDevelopment());
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

    // RAG-извлечение с обязательным фильтром доступа на стороне БД (ADR-0007, ТБ-020, GATE-1) и
    // конфигурируемым порогом релевантности (ТО-мат-04).
    builder.Services.AddCoreRetrieval(builder.Configuration);

    // Грунтовка: ссылки только из извлечённых фрагментов; «по памяти» запрещено (ТБ-040, GATE-2).
    builder.Services.AddCoreGrounding();

    // Движок документов: извлечение текста из файлов (.txt/.docx/OCR сканов) для загрузки корпуса (Э4-01, ПОДГ-02).
    builder.Services.AddCoreDocuments(builder.Configuration);

    // Загрузка в корпус: fail-closed (без грифа/подразделения — отказ), идемпотентно (ТБ-024, ТНД-002).
    builder.Services.AddCoreIngestion();

    // RAG-оркестратор: запрос → retriever (фильтр доступа) → промпт+модель → грунтовка → ответ (ТО-мат-01).
    builder.Services.AddCoreRag(builder.Configuration);

    // Очередь фоновых ИИ-задач (Э4-20, §5.1.3): интеллектуальные операции — асинхронно, со статусом и
    // управляемой деградацией; при старте — восстановление осиротевших задач. Store — из AddCorePersistence.
    builder.Services.AddCoreBackgroundTasks();

    // --- Точка композиции профиля (ТО-прог-05/06). Только хост знает о конкретном профиле. ---
    var profile = new InspectorProfile();
    builder.Services.AddSingleton<IProfile>(profile);
    profile.RegisterServices(builder.Services, builder.Configuration);
    profile.RegisterDataContexts(builder.Services, builder.Configuration);
    foreach (var contributor in profile.ModelContributors)
    {
        contributor.Register(builder.Services, builder.Configuration);
    }

    // --- Аутентификация и авторизация (Э3-08, ТБ-010..016) — последний шаг композиции (ТО-прог-05). ---
    // Auth:Mode=Dev (только Development) — dev-заглушка без входа, чтобы каркас был запускаем без
    // учёток вообще; любое другое значение — обычный вход по логину и паролю (идентичность локальная,
    // §6.5), допуск — отдельно, из core.clearance.
    var devAuth = builder.Environment.IsDevelopment()
        && string.Equals(builder.Configuration["Auth:Mode"], "Dev", StringComparison.OrdinalIgnoreCase);

    builder.Services.AddCascadingAuthenticationState();

    if (devAuth)
    {
        builder.Services.AddScoped<IAccessContextProvider, DevAccessContextProvider>();
        builder.Services.AddScoped<AuthenticationStateProvider, DevAuthenticationStateProvider>();
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddScoped<ISubjectProvider, HttpSubjectProvider>();
        builder.Services.AddAuthorization(); // без фолбэк-политики: dev-режим запускаем без входа
    }
    else
    {
        // Cookie-сессия: HttpOnly, скользящий таймаут неактивности (ТБ-014). Cookie несёт ТОЛЬКО
        // идентификацию — допуск читается из БД на каждую операцию (ТБ-016, ClearanceAccessContextProvider).
        var idleMinutes = int.TryParse(builder.Configuration["Auth:SessionIdleMinutes"], out var idle) && idle > 0
            ? idle
            : 30;
        builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(options => AuthCookieConfiguration.Configure(
                options, idleMinutes, builder.Environment.IsDevelopment())); // режимные настройки — ТБ-010/014

        // ИДЕНТИЧНОСТЬ — ЛОКАЛЬНАЯ (Э4-35 §6.5 завершён 2026-08-07): проверка пароля по
        // core.app_user (Argon2id). Адаптер чужой БД СКИД выведен из решения вместе со строкой
        // ConnectionStrings:Skid и секретом Database:Passwords:Skid — приложение СКИД отключается,
        // держать её БД ради одних учёток бессмысленно (вопрос 4 Э4-35).
        //
        // Порт IExternalIdentityProvider СОХРАНЁН намеренно — это шов под будущий SSO: меняется не
        // порт, а то, что за ним стоит. Вход, cookie, штамп безопасности и ревалидация сессий
        // (ISC.AI.Web/Security) от смены источника идентичности не зависят.
        //
        // Учётки по умолчанию НЕТ и быть не должно (предустановленный пароль — открытая дверь на весь
        // срок эксплуатации). Первая учётка заводится служебной командой хоста create-account,
        // роль — режимом первичной настройки (§6.4.1).
        builder.Services.AddScoped<IExternalIdentityProvider, LocalIdentityProvider>();

        // Учётная запись по умолчанию (решение заказчика 2026-08-07): создаётся при старте, только
        // если реестр пользователей ПУСТ, и только с ВРЕМЕННЫМ паролем — до смены оболочка никуда
        // не пускает, а смена делает пароль из конфигурации недействительным. Подробности и границы
        // применимости — в DefaultAccountSeeder.
        builder.Services.AddHostedService<DefaultAccountSeeder>();

        builder.Services.AddHttpContextAccessor();
        builder.Services.AddScoped<LoginService>();
        builder.Services.AddScoped<ExternalIdentityRevalidator>();
        builder.Services.AddScoped<AuthenticationStateProvider, StampRevalidatingAuthenticationStateProvider>();

        // За обратным прокси/TLS-терминатором внутри контура RemoteIpAddress иначе указывал бы на сам
        // прокси для ВСЕХ запросов — троттлинг входа (ниже) делил бы один лимит на всю организацию.
        // Без настройки KnownProxies поведение НЕ меняется (заголовку не доверяют) — это включатель,
        // не обязательный шаг: заполняется при реальном развёртывании за прокси.
        var knownProxies = builder.Configuration.GetSection("ForwardedHeaders:KnownProxies").Get<string[]>() ?? [];
        if (knownProxies.Length > 0)
        {
            builder.Services.Configure<ForwardedHeadersOptions>(options =>
            {
                options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
                foreach (var proxy in knownProxies)
                {
                    if (IPAddress.TryParse(proxy, out var address))
                    {
                        options.KnownProxies.Add(address);
                    }
                }
            });
        }

        // «Кто вошёл» — отдельно от «что ему можно»: администрирование допусков и ролей обязано
        // работать ДО появления первой записи допуска, иначе чистый контур запирается (см. ISubjectProvider).
        builder.Services.AddScoped<ISubjectProvider, HttpSubjectProvider>();

        // Боевой контекст доступа: допуск из core.clearance на каждую операцию, fail-closed (ТБ-012/016/021).
        builder.Services.AddScoped<IAccessContextProvider, ClearanceAccessContextProvider>();

        // Троттлинг входа: защита от перебора паролей (в СКИД её нет — добавляем на своей стороне).
        builder.Services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy(AuthEndpoints.LoginRateLimitPolicy, context =>
                RateLimitPartition.GetFixedWindowLimiter(
                    context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 5,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0,
                    }));
        });

        // Запрет анонимного доступа (ТБ-010): фолбэк-политика «только аутентифицированные» + именованные
        // политики модулей из манифеста профиля (IModule.RequiredPolicy — данные, не типы: ядро профиль не знает).
        builder.Services.AddAuthorization(options =>
        {
            options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
            foreach (var policy in profile.Modules.Select(m => m.RequiredPolicy)
                         .Where(p => !string.IsNullOrWhiteSpace(p))
                         .Distinct(StringComparer.Ordinal))
            {
                options.AddPolicy(policy, b => b.RequireAuthenticatedUser());
            }
        });
    }

    var app = builder.Build();

    // СЛУЖЕБНАЯ КОМАНДА вместо запуска сервера: завести учётку для входа (Э4-35 §6.5).
    //   dotnet run --project src/core/ISC.AI.Web -- create-account <логин> [ФИО]
    //
    // ПОЧЕМУ КОМАНДА, А НЕ ЗАСЕВ (DataSeed). Засев создавал бы учётку САМ, при старте, и вопрос
    // упирался бы в пароль: захардкоженный или конфигурационный — открытая дверь на весь срок
    // эксплуатации (попадает в репозиторий и в копии конфигов); сгенерированный — некуда отдать
    // (в лог нельзя — их читают шире, чем учётные данные; в файл нельзя — пароль на диске; в консоль
    // бессмысленно — служба работает без наблюдателя). Плюс засев отрабатывает на каждом старте:
    // условие «только если пусто» означало бы, что после удаления всех учёток молча появляется
    // новый администратор с никому не известным паролем.
    // Создание входа в режимную систему обязано быть ОСОЗНАННЫМ действием человека с доступом
    // к серверу, а не побочным эффектом запуска.
    if (AccountCommand.Matches(args))
    {
        return await AccountCommand.RunAsync(app.Services, args);
    }

    if (!app.Environment.IsDevelopment())
    {
        app.UseExceptionHandler("/Error", createScopeForErrors: true);
        app.UseHsts();
    }

    app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
    app.UseHttpsRedirection();
    app.UseSerilogRequestLogging();

    if (!devAuth)
    {
        app.UseForwardedHeaders(); // до троттлинга — иначе он видит IP прокси, а не клиента (см. регистрацию выше)
        app.UseRateLimiter();
        app.UseAuthentication(); // до антифорджери и авторизации: токен и политика привязаны к субъекту
    }

    app.UseAntiforgery();
    app.UseAuthorization();

    // Статика (css/js/шрифты) доступна и на странице входа — анонимно.
    app.MapStaticAssets().AllowAnonymous();

    if (!devAuth)
    {
        app.MapAuthEndpoints();
    }

    // Сырые HTTP-эндпоинты профиля (этап 4.3 Э4-35: раздача файлов docflow) — на конкретном типе
    // profile (не через IProfile): композиция уже знает профиль, лишний метод в ядре не нужен.
    profile.MapEndpoints(app);

    // Сборки, содержащие страницы модулей профиля, — для маршрутизации хоста.
    Assembly[] moduleAssemblies = [.. profile.Modules.Select(m => m.ComponentType.Assembly).Distinct()];
    app.MapRazorComponents<App>()
        .AddInteractiveServerRenderMode()
        .AddAdditionalAssemblies(moduleAssemblies);

    Log.Information("Хост ISC.AI запущен с профилем {ProfileId} ({ProfileName})", profile.Id, profile.DisplayName);
    await app.RunAsync();
    return 0;
}
catch (Exception ex)
{
    Log.Fatal(ex, "Хост ISC.AI завершился аварийно при запуске");

    // Ненулевой код возврата: под службой/оркестратором молчаливый выход с нулём выглядел бы
    // как штатное завершение, и падение старта осталось бы незамеченным.
    return 1;
}
finally
{
    Log.CloseAndFlush();
}
