using ISC.AI.Abstractions.Enums;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ISC.AI.Abstractions.AI;

/// <summary>
/// Поставщик keyed-регистрации клиента модели по роли. Регистрирует клиента
/// (<c>IChatClient</c> для Draft/Analysis, <c>IEmbeddingGenerator&lt;string, …&gt;</c> для Embeddings,
/// <c>IEmbeddingGenerator&lt;DataContent, …&gt;</c> для ImageEmbeddings) в контейнере под ключом-ролью;
/// конкретное название модели ядру не известно (ТО-прог-02/03). Параметры подключения (внутренний
/// адрес, имя модели, файлы весов) берутся из конфигурации (ТБ-044); векторизатор изображений может
/// работать и в процессе (ONNX), без сетевого адреса.
/// </summary>
public interface IModelContributor
{
    /// <summary>Роль, для которой поставщик регистрирует клиента.</summary>
    ModelRole Role { get; }

    /// <summary>Регистрирует клиента модели в контейнере под ключом <see cref="Role"/>.</summary>
    void Register(IServiceCollection services, IConfiguration configuration);
}
