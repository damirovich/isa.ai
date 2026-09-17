using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.DocFlow.Data;
using ISC.AI.Modules.DocFlow.Domain.Enums;
using ISC.AI.Modules.DocFlow.Domain.Services;
using ISC.AI.Persistence;
using ISC.AI.Persistence.Entities;
using ISC.AI.Persistence.Security;
using Microsoft.EntityFrameworkCore;
using Pgvector.EntityFrameworkCore;
using Shouldly;
using Testcontainers.PostgreSql;

namespace ISC.AI.IntegrationTests.Persistence;

/// <summary>
/// Добавление назначения (§4.1) и переназначение исполнителя (§4.7) на реальном PostgreSQL,
/// этап 3.2 Э4-35. Требуется Docker.
/// </summary>
/// <remarks>
/// Тесты закрепляют ИМЕННО отличия от СКИД, найденные разбором исходника: запрет дубля подразделения
/// живёт в БД (в СКИД — только в форме), переназначение исполненного запрещено (в СКИД разрешалось из
/// любого статуса), переназначение на того же — не событие (в СКИД писало аудит и слало уведомление),
/// исполнитель обязан видеть подразделение назначения.
/// </remarks>
public sealed class AssignmentCommandsTests : IAsyncLifetime
{
    private static readonly AccessContext Author = new("42", 5, [5, 9]);

    private readonly PostgreSqlContainer _postgres = TestPostgres.Create();

    public Task InitializeAsync() => _postgres.StartAsync();

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    [Fact(DisplayName = "Добавление назначения §4.1: своё подразделение, дубль запрещён, «Хранение» отклоняется")]
    public async Task Add_assignment_rules()
    {
        var (store, typeStore, _) = await BuildAsync();

        var executionType = await typeStore.CreateAsync("Поручение", DocumentGroup.Execution, isActive: true);
        var storageType = await typeStore.CreateAsync("Справка", DocumentGroup.Storage, isActive: true);

        var document = await store.CreateAsync(
            new DocumentDraft("П-1", new DateOnly(2026, 8, 6), executionType!.Value, DocumentDirection.Incoming,
                null, "Поручение", null, null, DocumentPriority.Medium, 7, 0, 5, 42),
            [new AssignmentDraft(5, 11, new DateOnly(2026, 8, 20))],
            useCommonDeadline: false, commonDeadline: null, Author);
        document.Status.ShouldBe(DocumentWriteStatus.Ok);

        // Дубль подразделения запрещён — у документа уже есть назначение на подразделение 5.
        (await store.AddAssignmentAsync(document.DocumentId, new AssignmentDraft(5, 12, null), Author))
            .Status.ShouldBe(DocumentWriteStatus.AssignmentDivisionTaken);

        // Другое подразделение — можно; исполнитель и срок НЕобязательны («назначение на подразделение»).
        var added = await store.AddAssignmentAsync(
            document.DocumentId, new AssignmentDraft(9, AssigneeUserId: null, Deadline: null), Author);
        added.Status.ShouldBe(DocumentWriteStatus.Ok);
        added.Notice.ShouldNotBeNull().Assignments.ShouldHaveSingleItem().AssigneeUserId.ShouldBeNull();

        // Новое назначение — «Зарегистрировано», со СВОИМ (пустым) сроком: единый срок §4.1 на
        // добавленные позже назначения не распространяется.
        var card = await store.GetAsync(document.DocumentId, Author);
        var fresh = card.ShouldNotBeNull().Assignments.Single(a => a.DivisionId == 9);
        fresh.Status.ShouldBe(AssignmentStatus.Registered);
        fresh.Deadline.ShouldBeNull();

        // История нового назначения начинается с «переход ниоткуда», а не с пустоты.
        var timeline = await store.GetAssignmentTimelineAsync(fresh.Id, Author);
        timeline.ShouldNotBeNull().ShouldHaveSingleItem().Kind.ShouldBe(AssignmentEventKind.Created);

        // Документ группы «Хранение» назначений не принимает — иначе его агрегат уехал бы с «неприменимо».
        var storageDocument = await store.CreateAsync(
            new DocumentDraft("Х-1", new DateOnly(2026, 8, 6), storageType!.Value, DocumentDirection.Internal,
                null, "Справка", null, null, null, null, 0, 5, 42),
            [], useCommonDeadline: false, commonDeadline: null, Author);
        (await store.AddAssignmentAsync(storageDocument.DocumentId, new AssignmentDraft(5, null, null), Author))
            .Status.ShouldBe(DocumentWriteStatus.NotExecutionGroup);

        // Недоступный документ неотличим от несуществующего (WriteAccessRule).
        (await store.AddAssignmentAsync(
            document.DocumentId, new AssignmentDraft(7, null, null), new AccessContext("60", 0, [77])))
            .Status.ShouldBe(DocumentWriteStatus.NotFound);
    }

    [Fact(DisplayName = "Переназначение §4.7: срок и статус сохраняются, исполненное не переназначается, тот же — не событие")]
    public async Task Reassign_rules()
    {
        var (store, typeStore, _) = await BuildAsync();

        var typeId = await typeStore.CreateAsync("Поручение", DocumentGroup.Execution, isActive: true);
        var deadline = new DateOnly(2026, 8, 20);
        var document = await store.CreateAsync(
            new DocumentDraft("П-2", new DateOnly(2026, 8, 6), typeId!.Value, DocumentDirection.Incoming,
                null, "Поручение", null, null, DocumentPriority.Medium, 7, 0, 5, 42),
            [new AssignmentDraft(5, 11, deadline)],
            useCommonDeadline: false, commonDeadline: null, Author);
        var assignmentId = document.Notice.ShouldNotBeNull().Assignments.ShouldHaveSingleItem().AssignmentId;

        // Тот же исполнитель — менять нечего: Ok, но без данных для уведомления (в СКИД слалось
        // «вы назначены исполнителем» и писался аудит «было = стало»).
        var sameAssignee = await store.ReassignAssigneeAsync(assignmentId, 11, reason: null, Author);
        sameAssignee.Status.ShouldBe(DocumentWriteStatus.Ok);
        sameAssignee.Notice.ShouldBeNull();

        // Штатное переназначение: статус и срок НЕ меняются.
        var reassigned = await store.ReassignAssigneeAsync(assignmentId, 12, "исполнитель в отпуске", Author);
        reassigned.Status.ShouldBe(DocumentWriteStatus.Ok);
        var notice = reassigned.Notice.ShouldNotBeNull();
        notice.PreviousAssigneeUserId.ShouldBe(11);
        notice.NewAssigneeUserId.ShouldBe(12);
        notice.Deadline.ShouldBe(deadline);

        var card = await store.GetAsync(document.DocumentId, Author);
        var assignment = card.ShouldNotBeNull().Assignments.ShouldHaveSingleItem();
        assignment.AssigneeUserId.ShouldBe(12);
        assignment.Deadline.ShouldBe(deadline);
        assignment.Status.ShouldBe(AssignmentStatus.Registered);

        // Переназначение ВИДНО в ленте — в СКИД оно не попадало туда вовсе.
        var timeline = await store.GetAssignmentTimelineAsync(assignmentId, Author);
        var event_ = timeline.ShouldNotBeNull().Single(e => e.Kind == AssignmentEventKind.Reassigned);
        event_.FromUserId.ShouldBe(11);
        event_.ToUserId.ShouldBe(12);
        event_.Comment.ShouldBe("исполнитель в отпуске");
        event_.ActorUserId.ShouldBe(42);

        // Исполненное назначение не переназначается (в СКИД проходило из любого статуса).
        (await store.ChangeAssignmentStatusAsync(assignmentId, AssignmentStatus.InProgress, null, Author))
            .ShouldBe(DocumentWriteStatus.Ok);
        (await store.ChangeAssignmentStatusAsync(assignmentId, AssignmentStatus.Done, null, Author))
            .ShouldBe(DocumentWriteStatus.Ok);
        (await store.ReassignAssigneeAsync(assignmentId, 13, null, Author))
            .Status.ShouldBe(DocumentWriteStatus.InvalidTransition);

        // Недоступное назначение неотличимо от несуществующего.
        (await store.ReassignAssigneeAsync(assignmentId, 13, null, new AccessContext("60", 0, [77])))
            .Status.ShouldBe(DocumentWriteStatus.NotFound);
        (await store.ReassignAssigneeAsync(999_999, 13, null, Author))
            .Status.ShouldBe(DocumentWriteStatus.NotFound);
    }

    [Fact(DisplayName = "Исполнитель обязан видеть подразделение назначения — иначе ему поручат невидимое")]
    public async Task Assignee_must_be_allowed_the_division()
    {
        var coreFactory = new CoreContextFactory(_postgres.GetConnectionString());
        var docFlowFactory = new DocFlowContextFactory(_postgres.GetConnectionString());
        await using (var db = docFlowFactory.CreateDbContext())
        {
            await db.Database.MigrateAsync();
        }

        int allowedId, strangerId;
        await using (var core = coreFactory.CreateDbContext())
        {
            await core.Database.MigrateAsync();
            var allowed = new AppUserEntity { UserName = "allowed", DisplayName = "Свой С.С." };
            var stranger = new AppUserEntity { UserName = "stranger", DisplayName = "Чужой Ч.Ч." };
            core.Users.AddRange(allowed, stranger);
            await core.SaveChangesAsync();
            (allowedId, strangerId) = (allowed.Id, stranger.Id);
        }

        // Настоящий справочник, а не заглушка: правило именно про допуск, подменять его нельзя.
        var users = new UserDirectory(coreFactory);
        var clearances = new ClearanceStore(coreFactory);
        (await clearances.SetAsync(allowedId, 5, [5])).ShouldBeTrue();
        (await clearances.SetAsync(strangerId, 5, [9])).ShouldBeTrue();

        var typeStore = new DocumentTypeStore(docFlowFactory);
        var store = new DocumentStore(
            docFlowFactory, new NoFileStorage(), new AllowAllAccessPolicy(), users);

        var typeId = await typeStore.CreateAsync("Поручение", DocumentGroup.Execution, isActive: true);
        var document = await store.CreateAsync(
            new DocumentDraft("П-3", new DateOnly(2026, 8, 6), typeId!.Value, DocumentDirection.Incoming,
                null, "Поручение", null, null, DocumentPriority.Medium, 7, 0, 5, 42),
            [new AssignmentDraft(5, allowedId, null)],
            useCommonDeadline: false, commonDeadline: null, Author);
        document.Status.ShouldBe(DocumentWriteStatus.Ok);
        var assignmentId = document.Notice.ShouldNotBeNull().Assignments.ShouldHaveSingleItem().AssignmentId;

        // Чужому подразделение 5 не разрешено — переназначить на него нельзя.
        (await store.ReassignAssigneeAsync(assignmentId, strangerId, null, Author))
            .Status.ShouldBe(DocumentWriteStatus.AssigneeOutsideDivision);

        // И добавить назначение с ним в исполнителях — тоже.
        (await store.AddAssignmentAsync(document.DocumentId, new AssignmentDraft(5, strangerId, null), Author))
            .Status.ShouldBe(DocumentWriteStatus.AssignmentDivisionTaken); // дубль ловится раньше
        (await store.AddAssignmentAsync(document.DocumentId, new AssignmentDraft(9, allowedId, null), Author))
            .Status.ShouldBe(DocumentWriteStatus.AssigneeOutsideDivision);
    }

    private async Task<(DocumentStore Store, DocumentTypeStore Types, DocFlowContextFactory Factory)> BuildAsync()
    {
        var factory = new DocFlowContextFactory(_postgres.GetConnectionString());
        await using (var db = factory.CreateDbContext())
        {
            await db.Database.MigrateAsync();
        }

        return (
            new DocumentStore(factory, new NoFileStorage(), new AllowAllAccessPolicy(), TestUserDirectory.AllowAll),
            new DocumentTypeStore(factory),
            factory);
    }
}
