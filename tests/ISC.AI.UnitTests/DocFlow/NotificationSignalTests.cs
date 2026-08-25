using System;
using System.Threading.Tasks;
using ISC.AI.Modules.DocFlow.Data;
using Shouldly;
using Xunit;

namespace ISC.AI.UnitTests.DocFlow;

/// <summary>
/// Шина живых уведомлений (INotificationSignal): сигнал приходит только подписчикам СВОЕГО
/// пользователя, отписка прекращает доставку, упавший подписчик не мешает остальным и не роняет
/// писателя. Содержимое через шину не идёт по построению — контракт несёт только колбэк «перечитай».
/// </summary>
public sealed class NotificationSignalTests
{
    // Publish — fire-and-forget: завершение колбэков ждём через TaskCompletionSource с таймаутом.
    private static Task<bool> WaitAsync(TaskCompletionSource<bool> tcs) =>
        Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(5)))
            .ContinueWith(t => t.Result == tcs.Task, TaskScheduler.Default);

    [Fact(DisplayName = "Сигнал приходит подписчику своего пользователя и не приходит чужому")]
    public async Task Signal_reaches_own_user_only()
    {
        var signal = new NotificationSignal();
        var mine = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var foreignDelivered = false;

        using var mySubscription = signal.Subscribe(1, () => { mine.TrySetResult(true); return Task.CompletedTask; });
        using var foreignSubscription = signal.Subscribe(2, () => { foreignDelivered = true; return Task.CompletedTask; });

        signal.Publish([1]);

        (await WaitAsync(mine)).ShouldBeTrue();
        foreignDelivered.ShouldBeFalse();
    }

    [Fact(DisplayName = "После отписки сигнал не доставляется")]
    public async Task Disposed_subscription_stops_delivery()
    {
        var signal = new NotificationSignal();
        var delivered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var subscription = signal.Subscribe(1, () => { delivered.TrySetResult(true); return Task.CompletedTask; });
        subscription.Dispose();

        signal.Publish([1]);

        (await WaitAsync(delivered)).ShouldBeFalse();
    }

    [Fact(DisplayName = "Упавший подписчик не мешает остальным и не роняет писателя")]
    public async Task Failing_subscriber_does_not_break_others()
    {
        var signal = new NotificationSignal();
        var healthy = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        // Умерший circuit бросает из колбэка — Publish обязан пережить это молча.
        using var broken = signal.Subscribe(1, () => throw new ObjectDisposedException("circuit"));
        using var alive = signal.Subscribe(1, () => { healthy.TrySetResult(true); return Task.CompletedTask; });

        Should.NotThrow(() => signal.Publish([1]));
        (await WaitAsync(healthy)).ShouldBeTrue();
    }

    [Fact(DisplayName = "Публикация без подписчиков — тихий no-op (уведомление ждёт адресата в БД)")]
    public void Publish_without_subscribers_is_noop()
    {
        var signal = new NotificationSignal();
        Should.NotThrow(() => signal.Publish([7, 8, 9]));
    }
}
