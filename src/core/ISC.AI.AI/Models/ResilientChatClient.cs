using ISC.AI.Abstractions.Enums;
using Microsoft.Extensions.AI;

namespace ISC.AI.AI.Models;

/// <summary>
/// Декоратор <see cref="IChatClient"/>: повтор, таймаут и circuit breaker вокруг вызовов сервера
/// инференса (ТН-003, ТНД-001) — см. <see cref="ModelCallResilience"/>.
/// </summary>
/// <remarks>
/// <see cref="GetStreamingResponseAsync"/> НЕ оборачивается: безопасный повтор частично полученного
/// потока — отдельная задача (требует буферизации/семантики возобновления), сейчас этот метод в конвейере
/// не используется (ТО-прог-01 — генератор стримить ещё не умеет).
/// </remarks>
internal sealed class ResilientChatClient(IChatClient inner, ModelRole role, TimeSpan callTimeout) : IChatClient
{
    private readonly ModelCallResilience _resilience = new(role, callTimeout);

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        _resilience.ExecuteAsync(ct => inner.GetResponseAsync(messages, options, ct), cancellationToken);

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        inner.GetStreamingResponseAsync(messages, options, cancellationToken);

    public object? GetService(Type serviceType, object? serviceKey = null) => inner.GetService(serviceType, serviceKey);

    public void Dispose() => inner.Dispose();
}
