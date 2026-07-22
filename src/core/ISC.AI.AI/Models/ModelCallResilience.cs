using System.Diagnostics;
using ISC.AI.Abstractions.AI;
using ISC.AI.Abstractions.Enums;

namespace ISC.AI.AI.Models;

/// <summary>
/// Повтор с задержкой, таймаут на вызов и простой circuit breaker вокруг обращений к серверу инференса
/// (ТН-003, ТНД-001) — недоступность модели не должна пробрасываться необработанным исключением в
/// интерактивную часть. Один экземпляр на роль модели: состояние circuit breaker не общее между ролями,
/// т.к. за разными ролями могут стоять разные серверы/инстансы (ADR-0011).
/// </summary>
/// <remarks>
/// Параметры повтора/circuit breaker необязательны — в проде используются дефолты; юнит-тесты передают
/// маленькие значения, чтобы не ждать реальные секунды на каждый прогон.
/// </remarks>
internal sealed class ModelCallResilience(
    ModelRole role,
    TimeSpan callTimeout,
    int maxAttempts = 3,
    int consecutiveFailuresToOpen = 5,
    TimeSpan? retryBaseDelay = null,
    TimeSpan? openDuration = null)
{
    private readonly TimeSpan _retryBaseDelay = retryBaseDelay ?? TimeSpan.FromMilliseconds(200);
    private readonly TimeSpan _openDuration = openDuration ?? TimeSpan.FromSeconds(30);

    private readonly object _gate = new();
    private int _consecutiveFailures;
    private DateTimeOffset _openUntil = DateTimeOffset.MinValue;

    /// <summary>
    /// Выполняет <paramref name="call"/> с таймаутом, повтором транзиентных сбоев и circuit breaker.
    /// </summary>
    /// <exception cref="ModelUnavailableException">
    /// Circuit breaker открыт, либо повторы исчерпаны — модель недоступна.
    /// </exception>
    public async Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> call, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (DateTimeOffset.UtcNow < _openUntil)
            {
                throw new ModelUnavailableException(role, innerException: null);
            }
        }

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            using var callCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            callCts.CancelAfter(callTimeout);

            try
            {
                var result = await call(callCts.Token);
                lock (_gate)
                {
                    _consecutiveFailures = 0;
                }

                return result;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw; // отмена самим вызывающим — не сбой сервера, повторять нельзя.
            }
            catch (Exception) when (attempt < maxAttempts)
            {
                // Наш таймаут (callCts) или сетевой/протокольный сбой — транзиентно, повторяем с задержкой.
                var delay = TimeSpan.FromMilliseconds(_retryBaseDelay.TotalMilliseconds * Math.Pow(2, attempt - 1));
                await Task.Delay(delay, cancellationToken);
            }
            catch (Exception ex)
            {
                lock (_gate)
                {
                    _consecutiveFailures++;
                    if (_consecutiveFailures >= consecutiveFailuresToOpen)
                    {
                        _openUntil = DateTimeOffset.UtcNow.Add(_openDuration);
                    }
                }

                throw new ModelUnavailableException(role, ex);
            }
        }

        throw new UnreachableException();
    }
}
