using System;
using System.Collections.Generic;
using System.Linq;
using ISC.AI.Modules.Chat;
using ISC.AI.Modules.Chat.Application;
using ISC.AI.Modules.Chat.Domain.Services;
using ISC.AI.Modules.Chat.UI;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using NSubstitute;
using Shouldly;
using Xunit;

namespace ISC.AI.UnitTests.Chat;

/// <summary>
/// Внешний контракт пакета «Чат» (ADR-0052): что обязан дать подключающий профиль (один порт), что — хост (нейтральные
/// службы ядра) и чего у пакета нет намеренно (своего слоя данных). Тот же принцип, что у <c>AdminContractTests</c>:
/// список обязанностей сверяется с ФАКТОМ регистрации, а не со словами в документации.
/// </summary>
/// <remarks>
/// Пакет выделен ради того, чтобы следующий профиль чат ПОДКЛЮЧАЛ, а не копировал. Цена подключения — длина списка
/// обязанностей, поэтому он закреплён числом и растёт только осознанно (правка теста вместе с правкой ADR).
/// </remarks>
public sealed class ChatContractTests
{
    private const string PortsNamespace = "ISC.AI.Modules.Chat.Domain.Services";

    [Fact(DisplayName = "Незарегистрированные порты домена — ровно порт профиля из манифеста (IChatProfile)")]
    public void Required_services_match_the_unregistered_ports()
    {
        var services = new ServiceCollection();
        ChatModule.RegisterServices(services);
        var registered = services.Select(descriptor => descriptor.ServiceType).ToHashSet();

        var declared = typeof(IChatProfile).Assembly.GetTypes()
            .Where(type => type.IsInterface && type.Namespace == PortsNamespace)
            .ToList();
        declared.ShouldNotBeEmpty("Порты пакета не найдены — проверь пространство имён домена.");

        var unregistered = declared.Where(port => !registered.Contains(port)).Select(port => port.Name).Order(StringComparer.Ordinal).ToList();
        var documented = ChatModule.RequiredServices.Select(type => type.Name).Order(StringComparer.Ordinal).ToList();

        unregistered.ShouldBe(
            documented,
            "Список обязанностей профиля разошёлся с фактом: либо порт забыли зарегистрировать в пакете, либо он внешний — "
            + "и тогда его место в ChatModule.RequiredServices, а подключающий профиль обязан его реализовать.");
        documented.ShouldBe([nameof(IChatProfile)]);
    }

    [Fact(DisplayName = "Пакет отдаёт экранам только настройки из порта профиля — сам порт экранам не нужен")]
    public void Settings_come_from_the_profile_port()
    {
        var settings = new ChatSettings { Subtitle = "Подзаголовок" };
        var profile = Substitute.For<IChatProfile>();
        profile.Settings.Returns(settings);
        var services = new ServiceCollection();
        ChatModule.RegisterServices(services);
        services.AddSingleton(profile);

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetRequiredService<ChatSettings>().ShouldBeSameAs(settings);
    }

    [Fact(DisplayName = "Обязанности хоста — только нейтральные службы ядра из Abstractions и не пересекаются с обязанностями профиля")]
    public void Required_core_services_are_neutral_core_ports()
    {
        ChatModule.RequiredCoreServices.ShouldAllBe(type => type.Assembly.GetName().Name == "ISC.AI.Abstractions");
        ChatModule.RequiredCoreServices.ShouldAllBe(type => type.IsInterface);
        ChatModule.RequiredCoreServices.ShouldContain(typeof(Abstractions.Conversations.IConversationStore));
        ChatModule.RequiredCoreServices.ShouldContain(typeof(Abstractions.Rag.IGroundedGenerator));
        ChatModule.RequiredCoreServices.ShouldContain(typeof(Abstractions.Rag.IConversationalGenerator));
        ChatModule.RequiredCoreServices.ShouldContain(typeof(Abstractions.Retrieval.IRetriever));
        ChatModule.RequiredCoreServices.ShouldContain(typeof(Abstractions.Audit.IAuditWriter));
        ChatModule.RequiredCoreServices.ShouldContain(typeof(Abstractions.Security.IAccessContextProvider));
        ChatModule.RequiredCoreServices.Count.ShouldBe(6);
        ChatModule.RequiredCoreServices.Intersect(ChatModule.RequiredServices).ShouldBeEmpty();
    }

    [Fact(DisplayName = "Порт пакета не тянет типы профиля — нейтральность не на словах")]
    public void Port_does_not_leak_profile_types()
    {
        var leaks = new List<string>();
        foreach (var port in typeof(IChatProfile).Assembly.GetTypes().Where(type => type.IsInterface && type.Namespace == PortsNamespace))
        {
            foreach (var method in port.GetMethods())
            {
                leaks.AddRange(method.GetParameters()
                    .Select(parameter => parameter.ParameterType)
                    .Append(method.ReturnType)
                    .SelectMany(Flatten)
                    .Select(type => type.Assembly.GetName().Name ?? string.Empty)
                    .Where(assembly => assembly.StartsWith("ISC.AI.Profile.", StringComparison.Ordinal))
                    .Select(assembly => $"{port.Name}.{method.Name} → {assembly}"));
            }
        }

        leaks.ShouldBeEmpty();
    }

    [Fact(DisplayName = "У пакета нет своего слоя данных: диалоги — в таблицах ядра через IConversationStore")]
    public void Package_has_no_data_layer()
    {
        var packageAssemblies = new[]
        {
            typeof(IChatProfile).Assembly,
            typeof(ChatMessages).Assembly,
            typeof(ChatPanel).Assembly,
            typeof(ChatModule).Assembly,
        };

        packageAssemblies
            .SelectMany(assembly => assembly.GetReferencedAssemblies()
                .Select(reference => (Package: assembly.GetName().Name, Reference: reference.Name ?? string.Empty)))
            .Where(pair => pair.Reference.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal)
                || pair.Reference.StartsWith("Npgsql", StringComparison.Ordinal)
                || pair.Reference == "ISC.AI.Persistence")
            .Select(pair => $"{pair.Package} → {pair.Reference}")
            .ShouldBeEmpty();
    }

    [Fact(DisplayName = "Страница чата: маршрут /chat пакета, политика пакета, название, секция и значок — профиля")]
    public void Page_entry_is_closed_by_policy()
    {
        var page = ChatModule.Page("Чат-ассистент", "ИИ-инструменты", Icons.Material.Filled.Forum);

        page.Id.ShouldBe("chat");
        page.Route.ShouldBe("/chat");
        page.RequiredPolicy.ShouldBe(ChatModule.ReadPolicy);
        page.MenuTitle.ShouldBe("Чат-ассистент");
        page.MenuGroup.ShouldBe("ИИ-инструменты");
        page.MenuIcon.ShouldBe(Icons.Material.Filled.Forum);
        page.ComponentType.ShouldBe(typeof(ChatPage));
        typeof(ChatPage).GetCustomAttributes(typeof(RouteAttribute), false).Cast<RouteAttribute>()
            .ShouldHaveSingleItem().Template.ShouldBe(ChatModule.Route);

        Should.Throw<ArgumentException>(() => ChatModule.Page(" ", "x", "y"));
        Should.Throw<ArgumentException>(() => ChatModule.Page("x", "", "y"));
    }

    private static IEnumerable<Type> Flatten(Type type)
    {
        yield return type;
        foreach (var argument in type.GetGenericArguments().SelectMany(Flatten))
        {
            yield return argument;
        }
    }
}
