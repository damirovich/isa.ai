using System;
using System.Collections.Generic;
using System.Linq;
using ISC.AI.Modules.DocFlow;
using ISC.AI.Modules.DocFlow.Data;
using ISC.AI.Modules.DocFlow.Domain.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace ISC.AI.UnitTests.DocFlow;

/// <summary>
/// Внешний контракт пакета модулей «Документооборот»: что обязан предоставить подключающий профиль.
/// </summary>
/// <remarks>
/// ЗАЧЕМ. Переиспользуемость модуля измеряется не тем, что он «ни на кого не ссылается» (это уже
/// закреплено архитектурными тестами), а тем, СКОЛЬКО нужно сделать, чтобы подключить его к следующему
/// профилю. Каждый новый порт в этом списке — ещё одна обязанность для эксплуатанта, и разрастаться
/// он должен только осознанно. Здесь список сверяется с фактом: с тем, что модуль объявил, но сам
/// не зарегистрировал.
/// </remarks>
public sealed class DocFlowContractTests
{
    [Fact(DisplayName = "Внешний контракт модуля — ровно три порта, и они перечислены в манифесте")]
    public void Required_services_match_the_unregistered_ports()
    {
        var services = new ServiceCollection();
        services.AddDocFlowPersistence(Configuration());
        var registered = services.Select(descriptor => descriptor.ServiceType).ToHashSet();

        // Порты, которые модуль ОБЪЯВИЛ в своём домене, но не реализовал сам, — и есть его контракт.
        var declared = typeof(IDocumentStore).Assembly.GetTypes()
            .Where(type => type.IsInterface
                && type.Namespace == "ISC.AI.Modules.DocFlow.Domain.Services");

        var unregistered = declared
            .Where(port => !registered.Contains(port))
            .Select(port => port.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        var documented = DocFlowModule.RequiredServices
            .Select(type => type.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        unregistered.ShouldBe(
            documented,
            "Список обязанностей профиля разошёлся с фактом. Либо порт забыли зарегистрировать "
            + "в AddDocFlowPersistence, либо он действительно внешний — и тогда его надо внести "
            + "в DocFlowModule.RequiredServices, а подключающий профиль обязан его реализовать.");

        // Три — не магическое число, а предел, который держим осознанно: справочник подразделений,
        // право администрирования и кандидаты в исполнители. Всё остальное модуль умеет сам.
        documented.Count.ShouldBe(3);
    }

    [Fact(DisplayName = "Модуль не требует от профиля ничего, кроме портов своего домена")]
    public void Module_does_not_demand_profile_types()
    {
        // Ни один из объявленных контрактов не должен ссылаться на типы профиля: иначе «нейтральный
        // порт» окажется нейтральным только на словах.
        var ports = typeof(IDocumentStore).Assembly.GetTypes()
            .Where(type => type.IsInterface
                && type.Namespace == "ISC.AI.Modules.DocFlow.Domain.Services");

        var leaks = new List<string>();
        foreach (var port in ports)
        {
            foreach (var method in port.GetMethods())
            {
                var types = method.GetParameters()
                    .Select(parameter => parameter.ParameterType)
                    .Append(method.ReturnType);

                leaks.AddRange(types
                    .Select(type => type.Assembly.GetName().Name ?? string.Empty)
                    .Where(assembly => assembly.StartsWith("ISC.AI.Profile.", StringComparison.Ordinal))
                    .Select(assembly => $"{port.Name}.{method.Name} → {assembly}"));
            }
        }

        leaks.ShouldBeEmpty();
    }

    /// <summary>Конфигурация со строкой подключения — контейнер собирается без обращения к базе.</summary>
    private static IConfiguration Configuration() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DocFlow"] = "Host=localhost;Database=test;Username=test;Password=test",
            })
            .Build();
}
