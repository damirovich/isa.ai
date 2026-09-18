using System;
using System.Collections.Generic;
using System.Linq;
using ISC.AI.Abstractions.Modules;
using ISC.AI.Modules.Admin;
using ISC.AI.Modules.Admin.Application;
using ISC.AI.Modules.Admin.Domain.Services;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace ISC.AI.UnitTests.Admin;

/// <summary>
/// Внешний контракт пакета модулей «Администрирование платформы» (ADR-0023): что обязан дать
/// подключающий профиль (порты профиля), что — хост (нейтральные службы ядра) и чего у пакета нет
/// намеренно. Тот же принцип, что у <c>DocFlowContractTests</c> и <c>MediaContractTests</c>: список
/// обязанностей сверяется с ФАКТОМ регистрации, а не со словами в документации.
/// </summary>
/// <remarks>
/// ЗАЧЕМ ИМЕННО ЭТОТ ТЕСТ. Пакет выделен ради того, чтобы следующий отдел администрирование ПОДКЛЮЧАЛ,
/// а не копировал (ADR-0002/0017). Цена подключения измеряется длиной списка обязанностей: каждый
/// новый порт — ещё одна обязанность эксплуатанта, поэтому список закреплён числом и растёт только
/// осознанно (правка теста вместе с правкой ADR).
/// </remarks>
public sealed class AdminContractTests
{
    /// <summary>Пространство имён портов пакета — единственное место, где они объявляются.</summary>
    private const string PortsNamespace = "ISC.AI.Modules.Admin.Domain.Services";

    [Fact(DisplayName = "Незарегистрированные порты домена — ровно порты профиля из манифеста (IPlatformAdministration, IDivisionCatalog, IUserRoleCatalog)")]
    public void Required_services_match_the_unregistered_ports()
    {
        var services = new ServiceCollection();
        AdminModule.RegisterServices(services);
        var registered = services.Select(descriptor => descriptor.ServiceType).ToHashSet();

        // Порты, которые пакет ОБЪЯВИЛ в своём домене, но сам не реализовал, — и есть его контракт.
        var declared = typeof(IPlatformAdministration).Assembly.GetTypes()
            .Where(type => type.IsInterface && type.Namespace == PortsNamespace)
            .ToList();
        declared.ShouldNotBeEmpty("Порты пакета не найдены — проверь пространство имён домена.");

        var unregistered = declared.Where(port => !registered.Contains(port))
            .Select(port => port.Name).OrderBy(name => name, StringComparer.Ordinal).ToList();
        var documented = AdminModule.RequiredServices
            .Select(type => type.Name).OrderBy(name => name, StringComparer.Ordinal).ToList();

        unregistered.ShouldBe(
            documented,
            "Список обязанностей профиля разошёлся с фактом: либо порт забыли зарегистрировать в пакете, "
            + "либо он действительно внешний — и тогда его место в AdminModule.RequiredServices, а "
            + "подключающий профиль обязан его реализовать.");

        // Три — не магическое число, а удерживаемый предел: право администрировать (определяется РОЛЬЮ),
        // наименования подразделений и роли для показа. Остальное пакет закрывает портами ядра.
        documented.Count.ShouldBe(3);
        AdminModule.RequiredServices.ShouldContain(typeof(IPlatformAdministration));
        AdminModule.RequiredServices.ShouldContain(typeof(IDivisionCatalog));
        AdminModule.RequiredServices.ShouldContain(typeof(IUserRoleCatalog));
    }

    [Fact(DisplayName = "Обязанности хоста — только нейтральные службы ядра из Abstractions, и они не пересекаются с обязанностями профиля")]
    public void Required_core_services_are_neutral_core_ports()
    {
        AdminModule.RequiredCoreServices.ShouldAllBe(
            type => type.Assembly.GetName().Name == "ISC.AI.Abstractions");
        AdminModule.RequiredCoreServices.ShouldAllBe(type => type.IsInterface);

        AdminModule.RequiredCoreServices.ShouldContain(typeof(Abstractions.Security.IUserAccountStore));
        AdminModule.RequiredCoreServices.ShouldContain(typeof(Abstractions.Security.IClearanceStore));
        AdminModule.RequiredCoreServices.ShouldContain(typeof(Abstractions.Security.ISubjectProvider));
        AdminModule.RequiredCoreServices.ShouldContain(typeof(Abstractions.Security.IAccessContextProvider));
        AdminModule.RequiredCoreServices.ShouldContain(typeof(Abstractions.Audit.IAuditReader));
        AdminModule.RequiredCoreServices.ShouldContain(typeof(Abstractions.Audit.IAuditWriter));
        AdminModule.RequiredCoreServices.Count.ShouldBe(6);

        // Порт либо нейтрален и живёт в ядре, либо зависит от модели ролей — и его даёт профиль.
        // Пересечение списков означало бы двоякое толкование ответственности при подключении.
        AdminModule.RequiredCoreServices.Intersect(AdminModule.RequiredServices).ShouldBeEmpty();
    }

    [Fact(DisplayName = "Порты пакета не тянут типы профиля — нейтральность не на словах")]
    public void Ports_do_not_leak_profile_types()
    {
        var ports = typeof(IPlatformAdministration).Assembly.GetTypes()
            .Where(type => type.IsInterface && type.Namespace == PortsNamespace);

        var leaks = new List<string>();
        foreach (var port in ports)
        {
            foreach (var method in port.GetMethods())
            {
                var types = method.GetParameters()
                    .Select(parameter => parameter.ParameterType)
                    .Append(method.ReturnType)
                    .SelectMany(Flatten);

                leaks.AddRange(types
                    .Select(type => type.Assembly.GetName().Name ?? string.Empty)
                    .Where(assembly => assembly.StartsWith("ISC.AI.Profile.", StringComparison.Ordinal))
                    .Select(assembly => $"{port.Name}.{method.Name} → {assembly}"));
            }
        }

        leaks.ShouldBeEmpty();
    }

    [Fact(DisplayName = "У пакета нет своего слоя данных: он администрирует данные ядра, а не собственные")]
    public void Package_has_no_data_layer()
    {
        // Данные пакета — таблицы схемы core (учётки, допуски, журнал), доступ к ним только через порты
        // ядра. EF/Npgsql здесь означал бы ВТОРОЙ путь к тем же таблицам в обход ядра: другой набор
        // проверок допуска и другой состав аудита на одних и тех же данных (ТД-005, ADR-0023).
        var packageAssemblies = new[]
        {
            typeof(IPlatformAdministration).Assembly,
            typeof(AdminGuard).Assembly,
            typeof(AdminModule).Assembly,
        };

        var dataDependencies = packageAssemblies
            .SelectMany(assembly => assembly.GetReferencedAssemblies()
                .Select(reference => (Package: assembly.GetName().Name, Reference: reference.Name ?? string.Empty)))
            .Where(pair => pair.Reference.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal)
                || pair.Reference.StartsWith("Npgsql", StringComparison.Ordinal)
                || pair.Reference == "ISC.AI.Persistence")
            .Select(pair => $"{pair.Package} → {pair.Reference}")
            .ToList();

        dataDependencies.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Каждая страница пакета закрыта политикой доступа и лежит в одной секции меню")]
    public void Pages_are_closed_by_policy()
    {
        // Страница администрирования БЕЗ политики — открытый экран учётных записей и журнала для любого
        // аутентифицированного пользователя. Право проверяется и в сценарии (IPlatformAdministration),
        // но меню с маршрутом закрываются здесь, и пустая политика в реестре недопустима.
        AdminModule.Modules.ShouldNotBeEmpty();
        foreach (var module in AdminModule.Modules)
        {
            module.RequiredPolicy.ShouldBe(
                AdminModule.ReadPolicy, $"Страница «{module.MenuTitle}» должна быть закрыта политикой пакета.");
            module.MenuGroup.ShouldBe(
                AdminModule.MenuGroup, $"Страница «{module.MenuTitle}» должна лежать в секции «{AdminModule.MenuGroup}».");
            module.Route.ShouldStartWith(
                "/admin/", Case.Sensitive, $"Маршрут страницы «{module.MenuTitle}» должен быть в /admin/.");
        }

        AdminModule.Modules.Select(module => module.Id).ShouldBeUnique();
        AdminModule.Modules.Select(module => module.Route).ShouldBeUnique();
        AdminModule.ShellWidgets.ShouldHaveSingleItem().Slot.ShouldBe(ShellWidgetSlot.AppBarRight);
    }

    /// <summary>
    /// Разворачивает обобщённый тип в составляющие (<c>Task&lt;IReadOnlyList&lt;T&gt;&gt;</c> → <c>T</c>):
    /// без этого утечка профиля спряталась бы в аргументе обобщения, ведь сборкой самого <c>Task</c>
    /// числится библиотека платформы.
    /// </summary>
    private static IEnumerable<Type> Flatten(Type type)
    {
        yield return type;
        foreach (var argument in type.GetGenericArguments().SelectMany(Flatten))
        {
            yield return argument;
        }
    }
}
