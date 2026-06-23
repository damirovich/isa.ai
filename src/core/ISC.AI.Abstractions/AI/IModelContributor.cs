using ISC.AI.Abstractions.Enums;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ISC.AI.Abstractions.AI;

/// <summary>
/// Поставщик keyed-регистрации клиента модели по роли. Регистрирует клиента
/// (<c>IChatClient</c> для Draft/Analysis, <c>IEmbeddingGenerator</c> для Embeddings)
/// в контейнере под ключом-ролью; конкретное название модели ядру не известно (ТО-прог-02/03).
/// Параметры подключения к vLLM (внутренний адрес, имя модели) берутся из конфигурации (ТБ-044).
/// </summary>
public interface IModelContributor
{
    /// <summary>Роль, для которой поставщик регистрирует клиента.</summary>
    ModelRole Role { get; }

    /// <summary>Регистрирует клиента модели в контейнере под ключом <see cref="Role"/>.</summary>
    void Register(IServiceCollection services, IConfiguration configuration);
}
