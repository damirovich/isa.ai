using ISC.AI.AI.Models;
using ISC.AI.Abstractions.Enums;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;

namespace ISC.AI.UnitTests.Ai;

/// <summary>
/// Тесты регистрации моделей (ADR-0004, ТО-прог-02/03): клиенты регистрируются KEYED по роли и
/// разрешаются по ключу-роли; имена моделей берутся из конфигурации. Создание клиента сетевого
/// соединения не открывает — тест offline (без сервера инференса).
/// </summary>
public sealed class ModelRegistrationTests
{
    [Fact(DisplayName = "Модели регистрируются keyed по роли и разрешаются по ключу")]
    public void Models_are_registered_keyed_by_role()
    {
        var configuration = Substitute.For<IConfiguration>();
        configuration["Llm:Models:Draft:Endpoint"].Returns("http://10.0.0.1:9000/v1");
        configuration["Llm:Models:Draft:Model"].Returns("gemma");
        configuration["Llm:Models:Analysis:Endpoint"].Returns("http://10.0.0.1:9000/v1");
        configuration["Llm:Models:Analysis:Model"].Returns("gemma");
        configuration["Llm:Models:Embeddings:Endpoint"].Returns("http://10.0.0.1:9001/v1");
        configuration["Llm:Models:Embeddings:Model"].Returns("embeddinggemma");

        using var provider = new ServiceCollection()
            .AddCoreAiModels(configuration)
            .BuildServiceProvider();

        provider.GetKeyedService<IChatClient>(ModelRole.Draft).ShouldNotBeNull();
        provider.GetKeyedService<IChatClient>(ModelRole.Analysis).ShouldNotBeNull();
        provider.GetKeyedService<IEmbeddingGenerator<string, Embedding<float>>>(ModelRole.Embeddings).ShouldNotBeNull();
    }

    [Fact(DisplayName = "Несконфигурированная роль не регистрируется (fail-closed)")]
    public void Unconfigured_role_is_not_registered()
    {
        var configuration = Substitute.For<IConfiguration>(); // ничего не настроено

        using var provider = new ServiceCollection()
            .AddCoreAiModels(configuration)
            .BuildServiceProvider();

        provider.GetKeyedService<IChatClient>(ModelRole.Draft).ShouldBeNull();
    }
}
