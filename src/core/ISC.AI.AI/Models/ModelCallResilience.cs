using System.Diagnostics;
using System.Runtime.CompilerServices;
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
                RegisterFailure();
                throw new ModelUnavailableException(role, ex);
            }
        }

        throw new UnreachableException();
    }

    /// <summary>
    /// Обёртка ПОТОКОВОГО вызова (ТН-003/ТНД-001): circuit breaker на старте (как у блокирующего пути) и
    /// таймаут БЕЗДЕЙСТВИЯ между чанками — сервер принял запрос, но «завис» и не отдаёт очередной токен —
    /// с учётом сбоя в circuit breaker. Повтора здесь НЕТ: часть токенов уже могла уйти наружу, безопасно
    /// перезапустить поток нельзя (возобновление частично отданного потока — отдельная семантика, ТО-прог-01).
    /// Ошибка/таймаут потока пробрасывается типизированным <see cref="ModelUnavailableException"/>; отмена
    /// самим вызывающим — как есть.
    /// </summary>
    public async IAsyncEnumerable<T> ExecuteStreamingAsync<T>(
        Func<CancellationToken, IAsyncEnumerable<T>> call,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (DateTimeOffset.UtcNow < _openUntil)
            {
                throw new ModelUnavailableException(role, innerException: null);
            }
        }

        // Отдельный таймаут-источник, залинкованный в токен перечислителя: CancelAfter перед ожиданием
        // очередного чанка задаёт дедлайн на его ПРИХОД; после получения дедлайн снимается на время передачи
        // чанка потребителю (медленный потребитель не должен считаться «зависшим сервером»). Ретрая нет,
        // поэтому единый источник переиспользуется на весь поток.
        using var timeoutCts = new CancellationTokenSource();
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        await using var enumerator = call(linkedCts.Token).GetAsyncEnumerator(linkedCts.Token);

        while (true)
        {
            bool hasNext;
            timeoutCts.CancelAfter(callTimeout); // (пере)взводим таймаут бездействия перед ожиданием чанка
            try
            {
                hasNext = await enumerator.MoveNextAsync();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw; // отмена самим вызывающим — не сбой сервера, не оборачиваем
            }
            catch (Exception ex)
            {
                RegisterFailure();
                // Таймаут бездействия приходит как отмена по timeoutCts (не по вызывающему) — отделяем причиной.
                var reason = timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested
                    ? new TimeoutException($"Сервер модели не отдал очередной фрагмент потока за {callTimeout}.", ex)
                    : ex;
                throw new ModelUnavailableException(role, reason);
            }

            timeoutCts.CancelAfter(Timeout.InfiniteTimeSpan); // снимаем дедлайн на время передачи чанка потребителю

            if (!hasNext)
            {
                break;
            }

            yield return enumerator.Current;
        }

        lock (_gate)
        {
            _consecutiveFailures = 0; // поток дошёл до конца без сбоя
        }
    }

    // Учёт последовательного сбоя обращения к модели; после порога — «открываем» circuit breaker на время.
    private void RegisterFailure()
    {
        lock (_gate)
        {
            _consecutiveFailures++;
            if (_consecutiveFailures >= consecutiveFailuresToOpen)
            {
                _openUntil = DateTimeOffset.UtcNow.Add(_openDuration);
            }
        }
    }
}
