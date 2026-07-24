using System.Runtime.CompilerServices;
using ISC.AI.Abstractions.AI;
using ISC.AI.Abstractions.Enums;
using ISC.AI.AI.Models;
using Shouldly;

namespace ISC.AI.UnitTests.Models;

/// <summary>
/// ТН-003/ТНД-001: повтор, таймаут и circuit breaker вокруг вызовов сервера инференса — недоступность
/// модели не должна пробрасываться необработанным исключением, а собственная отмена вызывающего не
/// должна повторяться.
/// </summary>
public sealed class ModelCallResilienceTests
{
    [Fact(DisplayName = "Успешный вызов с первой попытки — без повтора")]
    public async Task Succeeds_without_retry_when_call_succeeds_first_try()
    {
        var resilience = new ModelCallResilience(ModelRole.Draft, TimeSpan.FromSeconds(5));
        var calls = 0;

        var result = await resilience.ExecuteAsync(
            _ => { calls++; return Task.FromResult("ok"); }, CancellationToken.None);

        result.ShouldBe("ok");
        calls.ShouldBe(1);
    }

    [Fact(DisplayName = "Транзиентный сбой — повтор, затем успех")]
    public async Task Retries_transient_failure_then_succeeds()
    {
        var resilience = new ModelCallResilience(
            ModelRole.Draft, TimeSpan.FromSeconds(5), maxAttempts: 3, retryBaseDelay: TimeSpan.FromMilliseconds(1));
        var calls = 0;

        var result = await resilience.ExecuteAsync(
            _ =>
            {
                calls++;
                return calls < 2 ? throw new InvalidOperationException("сеть недоступна") : Task.FromResult("ok");
            },
            CancellationToken.None);

        result.ShouldBe("ok");
        calls.ShouldBe(2);
    }

    [Fact(DisplayName = "Повторы исчерпаны — ModelUnavailableException с ролью и исходным исключением")]
    public async Task Throws_ModelUnavailableException_after_exhausting_retries()
    {
        var resilience = new ModelCallResilience(
            ModelRole.Analysis, TimeSpan.FromSeconds(5), maxAttempts: 3, retryBaseDelay: TimeSpan.FromMilliseconds(1));
        var calls = 0;
        var originalError = new InvalidOperationException("сервер лёг");

        var thrown = await Should.ThrowAsync<ModelUnavailableException>(
            () => resilience.ExecuteAsync<string>(_ => { calls++; throw originalError; }, CancellationToken.None));

        thrown.Role.ShouldBe(ModelRole.Analysis);
        thrown.InnerException.ShouldBe(originalError);
        calls.ShouldBe(3);
    }

    [Fact(DisplayName = "Отмена самим вызывающим — не повторяется, пробрасывается как есть")]
    public async Task Does_not_retry_callers_own_cancellation()
    {
        var resilience = new ModelCallResilience(
            ModelRole.Draft, TimeSpan.FromSeconds(5), maxAttempts: 3, retryBaseDelay: TimeSpan.FromMilliseconds(1));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var calls = 0;

        await Should.ThrowAsync<OperationCanceledException>(
            () => resilience.ExecuteAsync<string>(
                ct => { calls++; ct.ThrowIfCancellationRequested(); return Task.FromResult("unreachable"); },
                cts.Token));

        // Отмена НЕ повторяется: делегат вызван не больше одного раза (при отмене ДО занятия слота bulkhead
        // короткозамыкает на WaitAsync и делегат не вызывается вовсе — это корректно: незачем идти к серверу).
        calls.ShouldBeLessThanOrEqualTo(1);
    }

    [Fact(DisplayName = "Circuit breaker открывается после подряд идущих сбоев и отказывает быстро, без вызова")]
    public async Task Opens_circuit_after_consecutive_failures_and_fails_fast()
    {
        var resilience = new ModelCallResilience(
            ModelRole.Embeddings, TimeSpan.FromSeconds(5), maxAttempts: 1, consecutiveFailuresToOpen: 2);
        var calls = 0;
        Task<string> Failing(CancellationToken _) { calls++; throw new InvalidOperationException("недоступен"); }

        await Should.ThrowAsync<ModelUnavailableException>(() => resilience.ExecuteAsync(Failing, CancellationToken.None));
        await Should.ThrowAsync<ModelUnavailableException>(() => resilience.ExecuteAsync(Failing, CancellationToken.None));
        calls.ShouldBe(2);

        // Третий вызов: circuit открыт — отказ мгновенно, делегат вообще не вызывается.
        await Should.ThrowAsync<ModelUnavailableException>(() => resilience.ExecuteAsync(Failing, CancellationToken.None));
        calls.ShouldBe(2);
    }

    [Fact(DisplayName = "Потоковый вызов: успешный поток проходит насквозь без изменений")]
    public async Task Streaming_passes_items_through_on_success()
    {
        var resilience = new ModelCallResilience(ModelRole.Draft, TimeSpan.FromSeconds(5));
        var received = new List<int>();

        await foreach (var i in resilience.ExecuteStreamingAsync(ct => Stream([1, 2, 3], ct), CancellationToken.None))
        {
            received.Add(i);
        }

        received.ShouldBe([1, 2, 3]);
    }

    [Fact(DisplayName = "Потоковый вызов: при открытом circuit breaker — отказ ДО обращения к серверу")]
    public async Task Streaming_fails_fast_when_circuit_open()
    {
        var resilience = new ModelCallResilience(
            ModelRole.Draft, TimeSpan.FromSeconds(5), maxAttempts: 1, consecutiveFailuresToOpen: 1);
        // Открываем circuit одним сбоем блокирующего вызова.
        await Should.ThrowAsync<ModelUnavailableException>(
            () => resilience.ExecuteAsync<string>(_ => throw new InvalidOperationException("лёг"), CancellationToken.None));

        var started = false;
        await Should.ThrowAsync<ModelUnavailableException>(async () =>
        {
            await foreach (var _ in resilience.ExecuteStreamingAsync(ct => Stream([1], ct), CancellationToken.None))
            {
                started = true;
            }
        });

        started.ShouldBeFalse(); // ни одного чанка не отдано — отказ до начала потока
    }

    [Fact(DisplayName = "Потоковый вызов: сбой посреди потока → ModelUnavailableException, отданные чанки сохранены")]
    public async Task Streaming_midstream_failure_wrapped_as_ModelUnavailable()
    {
        var resilience = new ModelCallResilience(ModelRole.Analysis, TimeSpan.FromSeconds(5));
        var received = new List<int>();

        var thrown = await Should.ThrowAsync<ModelUnavailableException>(async () =>
        {
            await foreach (var i in resilience.ExecuteStreamingAsync(ct => FailingStream(ct), CancellationToken.None))
            {
                received.Add(i);
            }
        });

        thrown.Role.ShouldBe(ModelRole.Analysis);
        received.ShouldBe([1]); // то, что успели получить до обрыва
    }

    [Fact(DisplayName = "Потоковый вызов: отмена вызывающим — OperationCanceledException, не оборачивается")]
    public async Task Streaming_caller_cancellation_not_wrapped()
    {
        var resilience = new ModelCallResilience(ModelRole.Draft, TimeSpan.FromSeconds(5));
        using var cts = new CancellationTokenSource();

        await Should.ThrowAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in resilience.ExecuteStreamingAsync(ct => Stream([1, 2, 3], ct), cts.Token))
            {
                cts.Cancel(); // отмена во время перечисления
            }
        });
    }

    [Fact(DisplayName = "Потоковый вызов: сервер завис (нет чанка дольше таймаута) → ModelUnavailableException")]
    public async Task Streaming_inactivity_timeout_fails()
    {
        var resilience = new ModelCallResilience(ModelRole.Draft, TimeSpan.FromMilliseconds(50));

        var thrown = await Should.ThrowAsync<ModelUnavailableException>(async () =>
        {
            await foreach (var _ in resilience.ExecuteStreamingAsync(ct => HangingStream(ct), CancellationToken.None))
            {
            }
        });

        thrown.Role.ShouldBe(ModelRole.Draft);
    }

    [Fact(DisplayName = "Bulkhead: одновременных вызовов к серверу не больше maxConcurrency")]
    public async Task Limits_concurrent_calls_to_max_concurrency()
    {
        var resilience = new ModelCallResilience(
            ModelRole.Draft, TimeSpan.FromSeconds(5), maxAttempts: 1, maxConcurrency: 2);

        var lockObj = new object();
        var current = 0;
        var maxObserved = 0;
        var gate = new TaskCompletionSource();

        async Task<string> Call(CancellationToken _)
        {
            lock (lockObj)
            {
                current++;
                if (current > maxObserved)
                {
                    maxObserved = current;
                }
            }

            await gate.Task; // держим слот занятым, пока не отпустим
            lock (lockObj)
            {
                current--;
            }

            return "ok";
        }

        var tasks = new Task<string>[4];
        for (var i = 0; i < tasks.Length; i++)
        {
            tasks[i] = resilience.ExecuteAsync(Call, CancellationToken.None);
        }

        await Task.Delay(100); // даём войти тем, кому хватило слотов
        lock (lockObj)
        {
            maxObserved.ShouldBeLessThanOrEqualTo(2); // третий/четвёртый ждут семафор, не сервер
        }

        gate.SetResult(); // отпускаем — остальные проходят по мере освобождения слотов
        await Task.WhenAll(tasks);
        maxObserved.ShouldBe(2); // лимит реально достигался (иначе bulkhead не работает)
    }

    private static async IAsyncEnumerable<int> Stream(
        IEnumerable<int> items, [EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var i in items)
        {
            ct.ThrowIfCancellationRequested();
            yield return i;
            await Task.Yield();
        }
    }

    private static async IAsyncEnumerable<int> FailingStream([EnumeratorCancellation] CancellationToken ct = default)
    {
        yield return 1;
        await Task.Yield();
        ct.ThrowIfCancellationRequested();
        throw new InvalidOperationException("поток оборвался");
    }

    private static async IAsyncEnumerable<int> HangingStream([EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.Delay(TimeSpan.FromSeconds(30), ct); // «зависший» сервер: молчит дольше таймаута бездействия
        yield return 1;
    }
}
