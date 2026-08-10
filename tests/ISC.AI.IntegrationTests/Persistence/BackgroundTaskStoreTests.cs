using ISC.AI.Abstractions.BackgroundTasks;
using ISC.AI.Persistence;
using ISC.AI.Persistence.BackgroundTasks;
using Microsoft.EntityFrameworkCore;
using Pgvector.EntityFrameworkCore;
using Shouldly;
using Testcontainers.PostgreSql;

namespace ISC.AI.IntegrationTests.Persistence;

/// <summary>
/// Интеграционные тесты персиста фоновых задач (Э4-20) на НАСТОЯЩЕМ PostgreSQL через Testcontainers
/// (EF InMemory не используется — ТО-прог-07): жизненный цикл статусов и восстановление осиротевших
/// задач после «перезапуска» (§5.1.4.5).
/// </summary>
/// <remarks>Требуется запущенный Docker.</remarks>
public sealed class BackgroundTaskStoreTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = TestPostgres.Create();

    public Task InitializeAsync() => _postgres.StartAsync();

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    [Fact(DisplayName = "Персист задач: жизненный цикл Queued→Running→Completed + восстановление осиротевших")]
    public async Task Lifecycle_and_recovery()
    {
        var factory = new CoreContextFactory(_postgres.GetConnectionString());
        await using (var db = factory.CreateDbContext())
        {
            await db.Database.MigrateAsync();
        }

        var store = new EfBackgroundTaskStore(factory);

        // Жизненный цикл одной задачи.
        var done = Guid.NewGuid();
        await store.CreateAsync(done, "генерация");
        (await store.GetAsync(done))!.Status.ShouldBe(BackgroundTaskStatus.Queued);

        await store.MarkRunningAsync(done);
        (await store.GetAsync(done))!.Status.ShouldBe(BackgroundTaskStatus.Running);

        await store.MarkCompletedAsync(done);
        var finished = await store.GetAsync(done);
        finished!.Status.ShouldBe(BackgroundTaskStatus.Completed);
        finished.FinishedAt.ShouldNotBeNull();

        // Осиротевшие: одна Queued + одна Running → восстановление помечает обе Failed, Completed не трогает.
        var orphanQueued = Guid.NewGuid();
        await store.CreateAsync(orphanQueued, "индексация");

        var orphanRunning = Guid.NewGuid();
        await store.CreateAsync(orphanRunning, "анализ");
        await store.MarkRunningAsync(orphanRunning);

        var recovered = await store.RecoverOrphanedAsync();

        recovered.ShouldBe(2);
        (await store.GetAsync(orphanQueued))!.Status.ShouldBe(BackgroundTaskStatus.Failed);
        (await store.GetAsync(orphanRunning))!.Status.ShouldBe(BackgroundTaskStatus.Failed);
        (await store.GetAsync(done))!.Status.ShouldBe(BackgroundTaskStatus.Completed); // завершённую не трогаем
    }
}
