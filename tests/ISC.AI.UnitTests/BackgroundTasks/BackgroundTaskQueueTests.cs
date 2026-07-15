using ISC.AI.AI.BackgroundTasks;
using ISC.AI.Abstractions.BackgroundTasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;

namespace ISC.AI.UnitTests.BackgroundTasks;

/// <summary>
/// Очередь фоновых задач (Э4-20): постановка → Queued; успешная работа → Completed; падение задачи →
/// Failed БЕЗ падения исполнителя (управляемая деградация §5.1.3.3/§5.1.4.1).
/// </summary>
public sealed class BackgroundTaskQueueTests
{
    /// <summary>In-memory заглушка персиста статусов (без БД).</summary>
    private sealed class InMemoryStore : IBackgroundTaskStore
    {
        private readonly Dictionary<Guid, BackgroundTaskInfo> _tasks = [];

        public BackgroundTaskInfo? Get(Guid id) => _tasks.GetValueOrDefault(id);

        public Task CreateAsync(Guid id, string kind, CancellationToken ct = default)
        {
            _tasks[id] = new BackgroundTaskInfo(id, kind, BackgroundTaskStatus.Queued, DateTime.UtcNow, null, null, null);
            return Task.CompletedTask;
        }

        public Task MarkRunningAsync(Guid id, CancellationToken ct = default) => Update(id, BackgroundTaskStatus.Running, null);
        public Task MarkCompletedAsync(Guid id, CancellationToken ct = default) => Update(id, BackgroundTaskStatus.Completed, null);
        public Task MarkFailedAsync(Guid id, string errorMessage, CancellationToken ct = default) => Update(id, BackgroundTaskStatus.Failed, errorMessage);
        public Task<BackgroundTaskInfo?> GetAsync(Guid id, CancellationToken ct = default) => Task.FromResult(Get(id));
        public Task<int> RecoverOrphanedAsync(CancellationToken ct = default) => Task.FromResult(0);

        private Task Update(Guid id, BackgroundTaskStatus status, string? error)
        {
            var task = _tasks[id];
            _tasks[id] = task with { Status = status, Error = error ?? task.Error };
            return Task.CompletedTask;
        }
    }

    private static (BackgroundTaskRunner Runner, ChannelBackgroundTaskQueue Queue, InMemoryStore Store) Build()
    {
        var store = new InMemoryStore();
        var queue = new ChannelBackgroundTaskQueue(store);
        var provider = new ServiceCollection().BuildServiceProvider();
        var runner = new BackgroundTaskRunner(
            queue, store, provider.GetRequiredService<IServiceScopeFactory>(),
            Substitute.For<ILogger<BackgroundTaskRunner>>());
        return (runner, queue, store);
    }

    [Fact(DisplayName = "Очередь: постановка → Queued; успешная работа → Completed")]
    public async Task Successful_task_completes()
    {
        var (runner, queue, store) = Build();

        var id = await queue.EnqueueAsync("тест", (_, _) => Task.CompletedTask);
        store.Get(id)!.Status.ShouldBe(BackgroundTaskStatus.Queued);

        await runner.RunOnceAsync(CancellationToken.None);

        store.Get(id)!.Status.ShouldBe(BackgroundTaskStatus.Completed);
    }

    [Fact(DisplayName = "Очередь: падение задачи → Failed, исполнитель не падает (деградация)")]
    public async Task Failing_task_marked_failed_without_throwing()
    {
        var (runner, queue, store) = Build();

        var id = await queue.EnqueueAsync("тест", (_, _) => throw new InvalidOperationException("сбой модели"));

        // Деградация: RunOnceAsync НЕ пробрасывает ошибку задачи.
        await Should.NotThrowAsync(async () => await runner.RunOnceAsync(CancellationToken.None));

        var info = store.Get(id)!;
        info.Status.ShouldBe(BackgroundTaskStatus.Failed);
        info.Error.ShouldNotBeNull().ShouldContain("сбой модели");
    }
}
