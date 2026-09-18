using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.Admin;
using ISC.AI.Modules.DocFlow;
using ISC.AI.Modules.Media;
using ISC.AI.Profile.Investigation;
using ISC.AI.Profile.Investigation.Data;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace ISC.AI.UnitTests.Investigation;

/// <summary>
/// Манифест профиля «Следствие» (ADR-0021/0022): реестр страниц, контракт подключённых пакетов и
/// обязательные маршруты оболочки. Тот же принцип, что у <c>DocFlowContractTests</c>/<c>MediaContractTests</c>:
/// обязанности профиля сверяются с фактом регистрации в контейнере, а не со словами в документации.
/// </summary>
public sealed class InvestigationProfileTests
{
    // Сборки, из которых профиль вправе брать страницы: свой UI и UI подключённых пакетов.
    private static readonly string[] AllowedUiAssemblies =
    [
        "ISC.AI.Profile.Investigation.UI",
        "ISC.AI.Modules.Media.UI",
        "ISC.AI.Modules.DocFlow.UI",
        "ISC.AI.Modules.Admin.UI",
    ];

    // Порты ВСЕХ подключённых пакетов: профиль обязан закрыть каждый, иначе приложение не стартует (ТС-013).
    private static readonly IReadOnlyList<Type> AllRequiredPorts =
    [
        .. DocFlowModule.RequiredServices,
        .. MediaModule.RequiredServices,
        .. AdminModule.RequiredServices,
    ];

    [Fact(DisplayName = "Манифест: Id/DisplayName заданы, реестр непуст, политики заполнены, маршруты уникальны, страницы — из сборок UI профиля и пакетов")]
    public void Registry_is_well_formed()
    {
        var profile = new InvestigationProfile();

        profile.Id.ShouldBe("investigation");
        profile.DisplayName.ShouldBe("СледствиеAI");
        profile.Modules.ShouldNotBeEmpty();

        profile.Modules.ShouldAllBe(m => !string.IsNullOrWhiteSpace(m.RequiredPolicy));
        profile.Modules.ShouldAllBe(m => !string.IsNullOrWhiteSpace(m.Route));
        profile.Modules.ShouldAllBe(m => !string.IsNullOrWhiteSpace(m.Id));

        var routes = profile.Modules.Select(m => m.Route).ToList();
        routes.Distinct(StringComparer.OrdinalIgnoreCase).Count().ShouldBe(routes.Count, "маршруты реестра должны быть уникальны");

        var ids = profile.Modules.Select(m => m.Id).ToList();
        ids.Distinct(StringComparer.Ordinal).Count().ShouldBe(ids.Count, "идентификаторы модулей должны быть уникальны");

        var foreign = profile.Modules
            .Select(m => m.ComponentType.Assembly.GetName().Name ?? string.Empty)
            .Where(name => !AllowedUiAssemblies.Contains(name, StringComparer.Ordinal))
            .Distinct()
            .ToList();
        foreign.ShouldBeEmpty("страницы реестра берутся только из UI профиля и подключённых пакетов");
    }

    [Fact(DisplayName = "Реестр: есть /dashboard и /cases; секции меню — «Дела», затем пакеты, затем «Администрирование»")]
    public void Registry_contains_profile_pages_in_expected_order()
    {
        var profile = new InvestigationProfile();

        profile.Modules.ShouldContain(m => m.Route == "/dashboard");
        profile.Modules.ShouldContain(m => m.Route == "/cases");
        profile.Modules.ShouldContain(m => m.Route == "/admin/roles");
        profile.Modules.ShouldContain(m => m.Route == "/admin/clearances");
        profile.Modules.ShouldContain(m => m.Route == "/admin/divisions");

        // Страницы пакетов подмешаны как есть — ни маршрут, ни компонент профиль не переопределяет.
        foreach (var module in DocFlowModule.Modules.Concat(AdminModule.Modules))
        {
            profile.Modules.ShouldContain(m => m.Route == module.Route && m.ComponentType == module.ComponentType);
        }

        // Учётные записи, допуски и журнал аудита пришли ИЗ ПАКЕТА (ADR-0023), а не из UI профиля.
        foreach (var route in new[] { "/admin/users", "/admin/clearances", "/admin/audit" })
        {
            profile.Modules.First(m => m.Route == route).ComponentType.Assembly.GetName().Name
                .ShouldBe("ISC.AI.Modules.Admin.UI");
        }

        // Порядок секций — порядок первого появления: «Дела» первой, «Администрирование» последней.
        var groups = profile.Modules.Select(m => m.MenuGroup).Where(g => g is not null).Distinct().ToList();
        groups.First().ShouldBe("Дела");
        groups.Last().ShouldBe("Администрирование");
        groups.ShouldContain(DocFlowModule.MenuGroup);
    }

    [Fact(DisplayName = "Контракт пакетов: после RegisterServices + RegisterDataContexts зарегистрированы все порты docflow, «Медиа» и «Администрирования» и IAccessPolicy профиля")]
    public void Profile_registers_every_required_port_of_attached_modules()
    {
        var profile = new InvestigationProfile();
        var services = new ServiceCollection();
        var configuration = Configuration();

        profile.RegisterServices(services, configuration);
        profile.RegisterDataContexts(services, configuration);

        var registered = services.Select(d => d.ServiceType).ToHashSet();

        var missing = AllRequiredPorts
            .Where(port => !registered.Contains(port))
            .Select(port => port.Name)
            .ToList();
        missing.ShouldBeEmpty("профиль обязан закрыть каждый порт подключённых пакетов, иначе приложение не стартует");

        // Политика доступа — профильная (переопределяет заглушку ядра; последняя регистрация побеждает).
        var policy = services.LastOrDefault(d => d.ServiceType == typeof(IAccessPolicy));
        policy.ShouldNotBeNull();
        policy.ImplementationType.ShouldBe(typeof(InvestigationAccessPolicy));
        policy.Lifetime.ShouldBe(ServiceLifetime.Singleton);

        // Самопроверка профиля на полной регистрации молчит — это она вызывается в конце RegisterDataContexts.
        Should.NotThrow(() => InvestigationProfile.EnsureModulePortsRegistered(services));
    }

    /// <summary>
    /// «Без порта приложение не стартует» обязано быть правдой в ЛЮБОМ окружении, а не только в Development,
    /// где контейнер ASP.NET валидирует граф при сборке: профиль сам проверяет контракт пакетов на регистрации.
    /// </summary>
    [Fact(DisplayName = "Контракт пакетов: без единой регистрации портов профиль отказывает с перечнем всех портов")]
    public void Ensure_ports_throws_naming_every_missing_port_when_nothing_is_registered()
    {
        var services = new ServiceCollection();

        var error = Should.Throw<InvalidOperationException>(() => InvestigationProfile.EnsureModulePortsRegistered(services));

        foreach (var port in AllRequiredPorts)
        {
            error.Message.ShouldContain(port.Name, Case.Sensitive, $"в сообщении должен быть назван порт {port.Name}");
        }
    }

    [Fact(DisplayName = "Контракт пакетов: при отсутствии ОДНОГО порта «Медиа» назван именно он, остальные — нет")]
    public void Ensure_ports_names_only_the_missing_port()
    {
        var services = new ServiceCollection();
        var missingPort = MediaModule.RequiredServices[0];
        foreach (var port in AllRequiredPorts.Where(p => p != missingPort))
        {
            // Достаточно факта регистрации: проверка смотрит на ServiceType, экземпляр никогда не создаётся.
            services.AddSingleton(port, _ => throw new NotSupportedException("экземпляр в тесте не нужен"));
        }

        var error = Should.Throw<InvalidOperationException>(() => InvestigationProfile.EnsureModulePortsRegistered(services));

        error.Message.ShouldContain(missingPort.Name, Case.Sensitive);
        foreach (var port in AllRequiredPorts.Where(p => p != missingPort))
        {
            error.Message.ShouldNotContain(port.Name, Case.Sensitive, $"зарегистрированный порт {port.Name} не должен значиться отсутствующим");
        }
    }

    [Fact(DisplayName = "Контракт пакетов: RegisterDataContexts на пустой коллекции (без RegisterServices) всё равно закрывает порты — проверка не бросает")]
    public void RegisterDataContexts_alone_registers_every_port()
    {
        var profile = new InvestigationProfile();
        var services = new ServiceCollection();

        // Порты живут в слое данных профиля: их регистрирует именно RegisterDataContexts, и он же
        // проверяет результат — исключения быть не должно.
        Should.NotThrow(() => profile.RegisterDataContexts(services, Configuration()));
    }

    [Fact(DisplayName = "Виджеты оболочки: кнопка смены пароля — из пакета администрирования, колокольчик уведомлений — из docflow")]
    public void Shell_widgets_include_password_and_docflow_bell()
    {
        var profile = new InvestigationProfile();

        profile.ShellWidgets.ShouldContain(w => w.Id == "account-password");
        profile.ShellWidgets.ShouldContain(w => w.Id == "docflow-notifications");
        foreach (var widget in DocFlowModule.ShellWidgets.Concat(AdminModule.ShellWidgets))
        {
            profile.ShellWidgets.ShouldContain(w => w.Id == widget.Id && w.ComponentType == widget.ComponentType);
        }

        profile.ShellWidgets.Select(w => w.Id).Distinct(StringComparer.Ordinal).Count().ShouldBe(profile.ShellWidgets.Count);

        // Своей копии виджета профиль больше не держит: кнопку даёт ПАКЕТ (ADR-0023) — оба профиля
        // получают один и тот же диалог смены пароля, а не две расходящиеся формы.
        profile.ShellWidgets.First(w => w.Id == "account-password").ComponentType.Assembly.GetName().Name
            .ShouldBe("ISC.AI.Modules.Admin.UI");
    }

    [Theory(DisplayName = "Обязательные маршруты оболочки существуют в сборках UI реестра (страницы с RouteAttribute)")]
    [InlineData("/account/password")]
    [InlineData("/docflow/dashboard")]
    public void Shell_required_routes_exist_as_pages(string route)
    {
        // Маршруты без пункта меню: принудительная смена временного пароля (RequirePasswordChange хоста —
        // страница ПАКЕТА администрирования, ADR-0023) и адрес колокольчика docflow (страница профиля).
        // Ищем по всем сборкам реестра: именно они попадают в маршрутизацию (Routes.razor), а в какой из
        // них лежит страница — вопрос раскладки по пакетам, а не обязательства перед оболочкой.
        var assemblies = new InvestigationProfile().Modules
            .Select(m => m.ComponentType.Assembly)
            .Distinct()
            .ToList();

        var templates = assemblies
            .SelectMany(assembly => assembly.GetTypes())
            .SelectMany(type => type.GetCustomAttributes<RouteAttribute>(inherit: false))
            .Select(attribute => attribute.Template)
            .ToList();

        templates.ShouldContain(route);
    }

    private static IConfiguration Configuration() =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Core"] = "Host=localhost;Database=test;Username=u",
            ["Vision:Detector:Path"] = "d.onnx",
            ["Vision:Detector:Sha256"] = "00",
            ["Vision:Embedder:Path"] = "e.onnx",
            ["Vision:Embedder:Sha256"] = "00",
        }).Build();
}
