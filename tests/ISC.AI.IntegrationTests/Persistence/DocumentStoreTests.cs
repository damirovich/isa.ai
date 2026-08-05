using ISC.AI.Modules.DocFlow.Data;
using ISC.AI.Modules.DocFlow.Domain.Enums;
using ISC.AI.Modules.DocFlow.Domain.Services;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Testcontainers.PostgreSql;

namespace ISC.AI.IntegrationTests.Persistence;

/// <summary>
/// Хранилище документов на реальном PostgreSQL (Э4-35 этап 3.1, ТЗ СКИД §3–4): регистрация
/// «Хранение»/«Исполнение», матрица переходов, продление срока, пересчёт агрегата. Требуется Docker.
/// </summary>
public sealed class DocumentStoreTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("pgvector/pgvector:pg16").Build();

    public Task InitializeAsync() => _postgres.StartAsync();

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    [Fact(DisplayName = "Документы: регистрация двух групп, переходы §4.5, продление §4.6, агрегат §4.3")]
    public async Task Document_lifecycle_end_to_end()
    {
        var factory = new TestContextFactory(_postgres.GetConnectionString());
        await using (var db = factory.CreateDbContext())
        {
            await db.Database.MigrateAsync();
        }

        var typeStore = new DocumentTypeStore(factory);
        var store = new DocumentStore(factory);

        var storageType = await typeStore.CreateAsync("Справка", DocumentGroup.Storage, isActive: true);
        var executionType = await typeStore.CreateAsync("Поручение", DocumentGroup.Execution, isActive: true);

        // «Хранение»: назначения не создаются, приоритет/инспектор обнуляются, агрегат — «не применимо».
        var storageDoc = await store.CreateAsync(
            new DocumentDraft("С-1", new DateOnly(2026, 8, 1), storageType!.Value, DocumentDirection.Incoming,
                null, "Справка о результатах", null, null, DocumentPriority.High, 77, 0, 10, 42),
            [], useCommonDeadline: false, commonDeadline: null);
        storageDoc.Status.ShouldBe(DocumentWriteStatus.Ok);
        var storageItem = (await store.ListAsync(new DocumentListFilter(TypeId: storageType.Value))).ShouldHaveSingleItem();
        storageItem.AggregatedStatus.ShouldBe(DocumentAggregatedStatus.NotApplicable);
        storageItem.Priority.ShouldBeNull();
        storageItem.AssignmentsCount.ShouldBe(0);

        // «Исполнение» без приоритета/инспектора/назначений — отказ (§3.2).
        (await store.CreateAsync(
            new DocumentDraft(null, new DateOnly(2026, 8, 2), executionType!.Value, DocumentDirection.Internal,
                null, "Поручение без назначений", null, null, null, null, 0, 10, 42),
            [], false, null)).Status.ShouldBe(DocumentWriteStatus.ExecutionFieldsMissing);

        // «Исполнение» с двумя назначениями и единым сроком: история Registered, агрегат «Зарегистрирован».
        var deadline = new DateOnly(2026, 9, 1);
        var executionDoc = await store.CreateAsync(
            new DocumentDraft("П-1", new DateOnly(2026, 8, 2), executionType.Value, DocumentDirection.Incoming,
                "ГИ", "Проверить исполнение приказа", null, null, DocumentPriority.High, 77, 2, 10, 42),
            [new AssignmentDraft(10, 101, null), new AssignmentDraft(20, null, null)],
            useCommonDeadline: true, commonDeadline: deadline);
        executionDoc.Status.ShouldBe(DocumentWriteStatus.Ok);

        int firstAssignment, secondAssignment;
        await using (var db = factory.CreateDbContext())
        {
            var assignments = await db.DocumentAssignments
                .Where(a => a.DocumentId == executionDoc.DocumentId).OrderBy(a => a.Id).ToListAsync();
            assignments.Count.ShouldBe(2);
            assignments.ShouldAllBe(a => a.Deadline == deadline);
            firstAssignment = assignments[0].Id;
            secondAssignment = assignments[1].Id;
            (await db.AssignmentStatusHistories.CountAsync(h => h.ToStatus == AssignmentStatus.Registered))
                .ShouldBe(2);
        }

        // Дубликат рег. номера — отказ.
        (await store.CreateAsync(
            new DocumentDraft("П-1", new DateOnly(2026, 8, 3), storageType.Value, DocumentDirection.Internal,
                null, "Дубль номера", null, null, null, null, 0, 10, 42),
            [], false, null)).Status.ShouldBe(DocumentWriteStatus.RegNumberTaken);

        // Переходы: недопустимый (§4.5) и ручное «Просрочено» (§4.2) отклоняются.
        (await store.ChangeAssignmentStatusAsync(firstAssignment, AssignmentStatus.Done, null, 42))
            .ShouldBe(DocumentWriteStatus.InvalidTransition);
        (await store.ChangeAssignmentStatusAsync(firstAssignment, AssignmentStatus.Overdue, null, 42))
            .ShouldBe(DocumentWriteStatus.OverdueIsAutomatic);

        // «Контроль»: контролёр фиксируется на входе и сбрасывается на выходе; агрегат — «В работе».
        (await store.ChangeAssignmentStatusAsync(firstAssignment, AssignmentStatus.InControl, "беру на контроль", 42))
            .ShouldBe(DocumentWriteStatus.Ok);
        await using (var db = factory.CreateDbContext())
        {
            (await db.DocumentAssignments.SingleAsync(a => a.Id == firstAssignment))
                .ControllerUserId.ShouldBe(42);
            (await db.Documents.SingleAsync(d => d.Id == executionDoc.DocumentId))
                .AggregatedStatus.ShouldBe(DocumentAggregatedStatus.InProgress);
        }

        (await store.ChangeAssignmentStatusAsync(firstAssignment, AssignmentStatus.InProgress, null, 42))
            .ShouldBe(DocumentWriteStatus.Ok);
        await using (var db = factory.CreateDbContext())
        {
            (await db.DocumentAssignments.SingleAsync(a => a.Id == firstAssignment))
                .ControllerUserId.ShouldBeNull();
        }

        // Исполнение первого назначения: агрегат остаётся «В работе»? Нет — второе ещё Registered →
        // по §4.3 «хотя бы одно В работе/Контроль» не выполняется, все не Done → «Зарегистрирован».
        (await store.ChangeAssignmentStatusAsync(firstAssignment, AssignmentStatus.Done, "готово", 42))
            .ShouldBe(DocumentWriteStatus.Ok);
        await using (var db = factory.CreateDbContext())
        {
            (await db.Documents.SingleAsync(d => d.Id == executionDoc.DocumentId))
                .AggregatedStatus.ShouldBe(DocumentAggregatedStatus.Registered);
        }

        // Продление §4.6: фиксируется старый/новый срок, статус автоматически «В работе», история пишется.
        var newDeadline = new DateOnly(2026, 10, 1);
        (await store.ExtendDeadlineAsync(secondAssignment, newDeadline, "объём работ вырос", 42))
            .ShouldBe(DocumentWriteStatus.Ok);
        await using (var db = factory.CreateDbContext())
        {
            var extended = await db.DocumentAssignments.SingleAsync(a => a.Id == secondAssignment);
            extended.Deadline.ShouldBe(newDeadline);
            extended.Status.ShouldBe(AssignmentStatus.InProgress);
            var extension = (await db.DeadlineExtensions.Where(e => e.AssignmentId == secondAssignment).ToListAsync())
                .ShouldHaveSingleItem();
            extension.OldDeadline.ShouldBe(deadline);
            extension.Reason.ShouldBe("объём работ вырос");
            (await db.Documents.SingleAsync(d => d.Id == executionDoc.DocumentId))
                .AggregatedStatus.ShouldBe(DocumentAggregatedStatus.InProgress);
        }

        // Карточка (§3.2): атрибуты и назначения одним запросом; несуществующий — null.
        var details = await store.GetAsync(executionDoc.DocumentId);
        details.ShouldNotBeNull();
        details.TypeName.ShouldBe("Поручение");
        details.Group.ShouldBe(DocumentGroup.Execution);
        details.Assignments.Count.ShouldBe(2);
        (await store.GetAsync(999_999)).ShouldBeNull();

        // Фильтры списка (§3.4): текст и агрегированный статус.
        (await store.ListAsync(new DocumentListFilter(Text: "приказ"))).ShouldHaveSingleItem()
            .RegNumber.ShouldBe("П-1");
        (await store.ListAsync(new DocumentListFilter(AggregatedStatus: DocumentAggregatedStatus.InProgress)))
            .ShouldHaveSingleItem().AssignmentsCount.ShouldBe(2);
    }

    // Контекст с теми же опциями, что в проде (snake_case + история миграций в схеме docflow).
    private sealed class TestContextFactory(string connectionString) : IDbContextFactory<DocFlowDbContext>
    {
        public DocFlowDbContext CreateDbContext() =>
            new(new DbContextOptionsBuilder<DocFlowDbContext>()
                .UseNpgsql(connectionString, npg =>
                    npg.MigrationsHistoryTable("__ef_migrations_history", DocFlowDbContext.Schema))
                .UseSnakeCaseNamingConvention()
                .Options);
    }
}
