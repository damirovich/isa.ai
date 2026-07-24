using ISC.AI.Abstractions.Enums;
using Microsoft.Extensions.AI;

namespace ISC.AI.AI.Models;

/// <summary>
/// Декоратор <see cref="IChatClient"/>: повтор, таймаут и circuit breaker вокруг вызовов сервера
/// инференса (ТН-003, ТНД-001) — см. <see cref="ModelCallResilience"/>.
/// </summary>
/// <remarks>
/// И блокирующий <see cref="GetResponseAsync"/>, и потоковый <see cref="GetStreamingResponseAsync"/> идут
/// через <see cref="ModelCallResilience"/>. У потока СВОЯ семантика (см. <c>ExecuteStreamingAsync</c>):
/// circuit breaker + таймаут бездействия между чанками, но БЕЗ повтора — безопасно перезапустить частично
/// отданный поток нельзя (возобновление потока — отдельная задача, ТО-прог-01).
/// </remarks>
internal sealed class ResilientChatClient(IChatClient inner, ModelRole role, TimeSpan callTimeout) : IChatClient
{
    private readonly ModelCallResilience _resilience = new(role, callTimeout);

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        _resilience.ExecuteAsync(ct => inner.GetResponseAsync(messages, options, ct), cancellationToken);

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        _resilience.ExecuteStreamingAsync(ct => inner.GetStreamingResponseAsync(messages, options, ct), cancellationToken);

    public object? GetService(Type serviceType, object? serviceKey = null) => inner.GetService(serviceType, serviceKey);

    public void Dispose() => inner.Dispose();
}
