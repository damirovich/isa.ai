using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.DocFlow.Data;
using ISC.AI.Modules.DocFlow.Domain.Enums;
using ISC.AI.Modules.DocFlow.Domain.Services;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Testcontainers.PostgreSql;

namespace ISC.AI.IntegrationTests.Persistence;

/// <summary>
/// Лента событий назначения (§4.8, этап 3.2 Э4-35) на реальном PostgreSQL: переходы статусов и
/// продления сроков сводятся в ОДНУ хронологию. Требуется Docker.
/// </summary>
/// <remarks>
/// История переходов писалась с этапа 3.1, но наружу не отдавалась — то есть данные копились, а
/// проверить их было нечем. Тест закрывает и это: он падает, если события перестанут сводиться,
/// потеряют файлы или разъедутся по порядку.
/// </remarks>
public sealed class AssignmentTimelineTests : IAsyncLifetime
{
    private static readonly AccessContext Author = new("42", 5, [5]);

    private readonly PostgreSqlContainer _postgres = TestPostgres.Create();

    public Task InitializeAsync() => _postgres.StartAsync();

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    [Fact(DisplayName = "Лента §4.8: создание, переходы и продление одной хронологией, с файлами и системным автором")]
    public async Task Timeline_merges_status_history_and_extensions()
    {
        var factory = new DocFlowContextFactory(_postgres.GetConnectionString());
        await using (var db = factory.CreateDbContext())
        {
            await db.Database.MigrateAsync();
        }

        var storage = new TempFileStorage();
        var typeStore = new DocumentTypeStore(factory);
        var store = new DocumentStore(factory, storage, new AllowAllAccessPolicy(), TestUserDirectory.AllowAll);

        var typeId = await typeStore.CreateAsync("Поручение", DocumentGroup.Execution, isActive: true);
        var created = await store.CreateAsync(
            new DocumentDraft("П-1", new DateOnly(2026, 8, 6), typeId!.Value, DocumentDirection.Incoming,
                null, "Поручение с историей", null, null, DocumentPriority.Medium, 7, 0, 5, 42),
            [new AssignmentDraft(5, 11, new DateOnly(2026, 8, 10))],
            useCommonDeadline: false, commonDeadline: null, Author);
        created.Status.ShouldBe(DocumentWriteStatus.Ok);

        var assignmentId = created.Notice.ShouldNotBeNull().Assignments.ShouldHaveSingleItem().AssignmentId;

        // Переход с файлом (§4.2: файл можно приложить на каждом переходе).
        var transitionFile = new UploadedFile("отчёт.pdf", "application/pdf", [1, 2, 3]);
        (await store.ChangeAssignmentStatusAsync(
            assignmentId, AssignmentStatus.InProgress, "взял в работу", Author, [transitionFile]))
            .ShouldBe(DocumentWriteStatus.Ok);

        // Продление с файлом-обоснованием (§4.6) — ДРУГАЯ таблица и ДРУГАЯ категория хранилища.
        var reasonFile = new UploadedFile("основание.pdf", "application/pdf", [4, 5]);
        (await store.ExtendDeadlineAsync(
            assignmentId, new DateOnly(2026, 9, 1), "смежники не отдали данные", Author, [reasonFile]))
            .ShouldBe(DocumentWriteStatus.Ok);

        var timeline = await store.GetAssignmentTimelineAsync(assignmentId, Author);
        timeline.ShouldNotBeNull();

        // Порядок хронологический, старые первыми — читается сверху вниз, как и положено истории.
        timeline.Select(e => e.OccurredAt).ShouldBeInOrder();

        // Создание назначения отличается от смены статуса отсутствием FromStatus.
        var first = timeline[0];
        first.Kind.ShouldBe(AssignmentEventKind.Created);
        first.FromStatus.ShouldBeNull();
        first.ToStatus.ShouldBe(AssignmentStatus.Registered);

        var transition = timeline.Single(e =>
            e.Kind == AssignmentEventKind.StatusChanged && e.ToStatus == AssignmentStatus.InProgress);
        transition.Comment.ShouldBe("взял в работу");
        transition.ActorUserId.ShouldBe(42);
        var attached = transition.Files.ShouldHaveSingleItem();
        attached.FileName.ShouldBe("отчёт.pdf");
        attached.Category.ShouldBe(FileCategories.StatusHistory);

        // Продление попадает в ту же ленту, со своими сроками и своей категорией файлов.
        var extension = timeline.Single(e => e.Kind == AssignmentEventKind.DeadlineExtended);
        extension.OldDeadline.ShouldBe(new DateOnly(2026, 8, 10));
        extension.NewDeadline.ShouldBe(new DateOnly(2026, 9, 1));
        extension.Comment.ShouldBe("смежники не отдали данные");
        extension.Files.ShouldHaveSingleItem().Category.ShouldBe(FileCategories.DeadlineExtensions);

        // Продление §4.6 возвращает назначение «В работу» — в ленте это отдельный переход.
        timeline.Count(e => e.Kind == AssignmentEventKind.StatusChanged).ShouldBeGreaterThanOrEqualTo(1);

        // Системный перевод в «Просрочено»: автор отсутствует, и это не «неизвестно», а «система».
        var overdue = await store.MarkOverdueAsync(new DateOnly(2026, 9, 2));
        overdue.ShouldNotBeEmpty();

        var afterOverdue = await store.GetAssignmentTimelineAsync(assignmentId, Author);
        var systemEvent = afterOverdue.ShouldNotBeNull()
            .Single(e => e.ToStatus == AssignmentStatus.Overdue);
        systemEvent.ActorUserId.ShouldBeNull();
    }

    [Fact(DisplayName = "Лента §4.8: чужое и несуществующее назначение неотличимы — обоим null")]
    public async Task Timeline_is_filtered_by_clearance()
    {
        var factory = new DocFlowContextFactory(_postgres.GetConnectionString());
        await using (var db = factory.CreateDbContext())
        {
            await db.Database.MigrateAsync();
        }

        var typeStore = new DocumentTypeStore(factory);
        var store = new DocumentStore(factory, new TempFileStorage(), new AllowAllAccessPolicy(), TestUserDirectory.AllowAll);

        var typeId = await typeStore.CreateAsync("Поручение ДСП", DocumentGroup.Execution, isActive: true);
        var created = await store.CreateAsync(
            new DocumentDraft("С-1", new DateOnly(2026, 8, 6), typeId!.Value, DocumentDirection.Incoming,
                null, "Под грифом", null, null, DocumentPriority.Medium, 7, 3, 5, 42),
            [new AssignmentDraft(5, 11, new DateOnly(2026, 8, 10))],
            useCommonDeadline: false, commonDeadline: null, Author);
        created.Status.ShouldBe(DocumentWriteStatus.Ok);

        var assignmentId = created.Notice.ShouldNotBeNull().Assignments.ShouldHaveSingleItem().AssignmentId;

        // Допуск ниже грифа документа — история недоступна, причём тем же ответом, что «нет такого».
        var lowClearance = new AccessContext("60", 0, [5]);
        (await store.GetAssignmentTimelineAsync(assignmentId, lowClearance)).ShouldBeNull();

        // Чужое подразделение — тот же результат.
        (await store.GetAssignmentTimelineAsync(assignmentId, new AccessContext("60", 5, [9]))).ShouldBeNull();

        // Несуществующее назначение — неотличимо от недоступного.
        (await store.GetAssignmentTimelineAsync(999_999, Author)).ShouldBeNull();

        // Автору с достаточным допуском история видна.
        (await store.GetAssignmentTimelineAsync(assignmentId, Author)).ShouldNotBeNull().ShouldNotBeEmpty();
    }
}
