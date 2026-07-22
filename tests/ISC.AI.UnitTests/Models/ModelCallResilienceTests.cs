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

        calls.ShouldBe(1);
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
}
