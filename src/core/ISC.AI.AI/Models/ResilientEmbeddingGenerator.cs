using ISC.AI.Abstractions.Enums;
using Microsoft.Extensions.AI;

namespace ISC.AI.AI.Models;

/// <summary>
/// Декоратор <see cref="IEmbeddingGenerator{TInput,TEmbedding}"/>: повтор, таймаут и circuit breaker
/// вокруг вызовов сервера эмбеддингов (ТН-003, ТНД-001) — см. <see cref="ModelCallResilience"/>.
/// </summary>
internal sealed class ResilientEmbeddingGenerator(
    IEmbeddingGenerator<string, Embedding<float>> inner, ModelRole role, TimeSpan callTimeout)
    : IEmbeddingGenerator<string, Embedding<float>>
{
    private readonly ModelCallResilience _resilience = new(role, callTimeout);

    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default) =>
        _resilience.ExecuteAsync(ct => inner.GenerateAsync(values, options, ct), cancellationToken);

    public object? GetService(Type serviceType, object? serviceKey = null) => inner.GetService(serviceType, serviceKey);

    public void Dispose() => inner.Dispose();
}
