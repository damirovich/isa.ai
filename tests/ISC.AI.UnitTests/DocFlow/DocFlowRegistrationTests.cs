using System;
using System.Collections.Generic;
using System.Linq;
using ISC.AI.Modules.DocFlow.Data;
using ISC.AI.Modules.DocFlow.Domain.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace ISC.AI.UnitTests.DocFlow;

/// <summary>
/// Полнота регистрации портов модуля документооборота в контейнере.
/// </summary>
/// <remarks>
/// ПОЧЕМУ ЭТОТ ТЕСТ СУЩЕСТВУЕТ. Забытая регистрация не ломает сборку: интерфейс есть, реализация есть,
/// компилятор доволен. Она проявляется только при запуске — и то не всегда: проверка контейнера
/// (<c>ValidateOnBuild</c>) включена лишь в Development, поэтому запуск без него проходит успешно,
/// а у разработчика приложение падает при старте. Именно так и случилось с <see cref="IDeadlineChecker"/>
/// (2026-08-07): реализация написана, регистрация потеряна, сборка и тесты зелёные, хост не стартует.
///
/// Правило простое: если у порта из <c>Domain.Services</c> есть реализация В ЭТОЙ ЖЕ сборке данных —
/// он ОБЯЗАН быть зарегистрирован. Порты, реализацию которых даёт профиль (справочник подразделений,
/// администрирование), под правило не попадают: в сборке данных модуля их реализаций нет.
/// </remarks>
public sealed class DocFlowRegistrationTests
{
    [Fact(DisplayName = "Каждый порт модуля, реализованный в слое данных, зарегистрирован в контейнере")]
    public void Every_implemented_port_is_registered()
    {
        var services = new ServiceCollection();
        services.AddDocFlowPersistence(Configuration());

        var registered = services.Select(descriptor => descriptor.ServiceType).ToHashSet();

        var dataAssembly = typeof(DocumentStore).Assembly;
        var ports = typeof(IDocumentStore).Assembly.GetTypes()
            .Where(type => type.IsInterface
                && type.Namespace == "ISC.AI.Modules.DocFlow.Domain.Services");

        var missing = new List<string>();
        foreach (var port in ports)
        {
            var hasImplementation = dataAssembly.GetTypes()
                .Any(type => type is { IsClass: true, IsAbstract: false } && port.IsAssignableFrom(type));

            if (hasImplementation && !registered.Contains(port))
            {
                missing.Add(port.Name);
            }
        }

        missing.ShouldBeEmpty(
            "Порт реализован в слое данных, но не зарегистрирован — приложение упадёт при старте: "
            + string.Join(", ", missing));
    }

    /// <summary>
    /// Конфигурация со строкой подключения: <c>AddDocFlowPersistence</c> её требует, но НЕ подключается —
    /// контейнер собирается без обращения к базе.
    /// </summary>
    private static IConfiguration Configuration() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DocFlow"] = "Host=localhost;Database=test;Username=test;Password=test",
            })
            .Build();
}
