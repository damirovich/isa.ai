using ISC.AI.Abstractions.Security;
using ISC.AI.AI.Security;
using ISC.AI.Modules.DocFlow.Data;
using ISC.AI.Modules.DocFlow.Domain.Enums;
using ISC.AI.Modules.DocFlow.Domain.Services;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Testcontainers.PostgreSql;

namespace ISC.AI.IntegrationTests.Persistence;

/// <summary>
/// Уведомления (разд. 5 ТЗ СКИД, этап 2.2b Э4-35) на реальном PostgreSQL: ДЕДУПЛИКАЦИЯ уведомлений
/// о сроках и фильтр допуска над лентой. Требуется Docker.
/// </summary>
/// <remarks>
/// Дедупликация проверяется первым делом сознательно: в СКИД на неё (DL-059) не было НИ ОДНОГО теста,
/// и её поломка не видна ни по логам, ни по экрану — она проявляется у пользователя ежечасным потоком
/// одинаковых уведомлений, то есть уже в эксплуатации.
/// </remarks>
public sealed class NotificationStoreTests : IAsyncLifetime
{
    // Допуск «видно всё» для подготовки данных: предмет проверок — уведомления, не регистрация.
    private static readonly AccessContext FullAccess = new("42", 10, [5]);

    private readonly PostgreSqlContainer _postgres = TestPostgres.Create();

    public Task InitializeAsync() => _postgres.StartAsync();

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    [Fact(DisplayName = "Уведомления разд. 5: повтор о том же сроке не создаётся, о новом сроке — создаётся")]
    public async Task Deadline_notifications_are_deduplicated_by_assignment_type_and_deadline()
    {
        var factory = new DocFlowContextFactory(_postgres.GetConnectionString());
        await using (var db = factory.CreateDbContext())
        {
            await db.Database.MigrateAsync();
        }

        var typeStore = new DocumentTypeStore(factory);
        var documents = new DocumentStore(factory, new NoFileStorage(), new AllowAllAccessPolicy(), TestUserDirectory.AllowAll);
        var notifications = new NotificationStore(factory, new AllowAllAccessPolicy(), new NotificationSignal());

        var typeId = await typeStore.CreateAsync("Поручение", DocumentGroup.Execution, isActive: true);
        var deadline = new DateOnly(2026, 8, 20);
        var created = await documents.CreateAsync(
            new DocumentDraft("П-1", new DateOnly(2026, 8, 6), typeId!.Value, DocumentDirection.Incoming,
                null, "Поручение с назначением", null, null, DocumentPriority.Medium, 7, 0, 5, 42),
            [new AssignmentDraft(5, 11, deadline)],
            useCommonDeadline: false, commonDeadline: null, FullAccess);
        created.Status.ShouldBe(DocumentWriteStatus.Ok);

        var card = await documents.GetAsync(created.DocumentId, FullAccess);
        var assignmentId = card!.Assignments.ShouldHaveSingleItem().Id;

        var approaching = new NotificationDraft(
            [11],
            NotificationType.DeadlineApproaching,
            NotificationTemplates.DeadlineApproaching,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["document"] = "П-1" },
            created.DocumentId,
            assignmentId,
            AboutDeadline: deadline);

        (await notifications.RaiseAsync(approaching)).ShouldBe(1);

        // Ключевой случай: фоновая проверка идёт раз в час — повтор о ТОМ ЖЕ сроке не должен родиться.
        (await notifications.RaiseAsync(approaching)).ShouldBe(0);
        (await notifications.RaiseAsync(approaching)).ShouldBe(0);

        // Другой ВИД уведомления по тому же назначению и сроку — самостоятельное уведомление.
        (await notifications.RaiseAsync(approaching with
        {
            Type = NotificationType.DeadlineToday,
            MessageKey = NotificationTemplates.DeadlineToday,
        })).ShouldBe(1);

        // Срок продлили — про НОВЫЙ срок предупредить обязаны (изъян скользящего окна СКИД, DL-059).
        (await notifications.RaiseAsync(approaching with { AboutDeadline = new DateOnly(2026, 9, 1) }))
            .ShouldBe(1);

        // Уведомления НЕ о сроках дедупликации не подлежат: два комментария — два уведомления.
        var comment = new NotificationDraft(
            [11],
            NotificationType.CommentAdded,
            NotificationTemplates.CommentAdded,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["document"] = "П-1", ["author"] = "А." },
            created.DocumentId);
        (await notifications.RaiseAsync(comment)).ShouldBe(1);
        (await notifications.RaiseAsync(comment)).ShouldBe(1);

        var assignee = new AccessContext("11", 10, [5]);
        (await notifications.CountUnreadAsync(assignee)).ShouldBe(5);

        // Текст собирается из ключа и аргументов при чтении, а не хранится готовым.
        var feed = await notifications.ListUnreadAsync(assignee);
        feed.ShouldContain(n => n.Message.Contains("П-1", StringComparison.Ordinal));

        // Чужое уведомление неотличимо от несуществующего; своё помечается идемпотентно.
        (await notifications.MarkReadAsync(feed[0].Id, recipientUserId: 999)).ShouldBeFalse();
        (await notifications.MarkReadAsync(feed[0].Id, recipientUserId: 11)).ShouldBeTrue();
        (await notifications.MarkReadAsync(feed[0].Id, recipientUserId: 11)).ShouldBeTrue();
        (await notifications.CountUnreadAsync(assignee)).ShouldBe(4);

        await notifications.MarkAllReadAsync(11);
        (await notifications.CountUnreadAsync(assignee)).ShouldBe(0);
    }

    [Fact(DisplayName = "Уведомления разд. 5: лента и счётчик скрывают уведомления о недоступных документах")]
    public async Task Feed_is_filtered_by_clearance_not_only_by_recipient()
    {
        var factory = new DocFlowContextFactory(_postgres.GetConnectionString());
        await using (var db = factory.CreateDbContext())
        {
            await db.Database.MigrateAsync();
        }

        var typeStore = new DocumentTypeStore(factory);
        var documents = new DocumentStore(factory, new NoFileStorage(), new AllowAllAccessPolicy(), TestUserDirectory.AllowAll);
        var notifications = new NotificationStore(factory, new AllowAllAccessPolicy(), new NotificationSignal());

        var typeId = await typeStore.CreateAsync("Справка", DocumentGroup.Storage, isActive: true);

        var open = await documents.CreateAsync(
            new DocumentDraft("О-1", new DateOnly(2026, 8, 6), typeId!.Value, DocumentDirection.Internal,
                null, "Открытый документ", null, null, null, null, 0, 5, 42),
            [], useCommonDeadline: false, commonDeadline: null, FullAccess);
        var secret = await documents.CreateAsync(
            new DocumentDraft("С-1", new DateOnly(2026, 8, 6), typeId.Value, DocumentDirection.Internal,
                null, "Документ под грифом", null, null, null, null, 3, 5, 42),
            [], useCommonDeadline: false, commonDeadline: null, FullAccess);
        var otherDivision = await documents.CreateAsync(
            new DocumentDraft("Д-9", new DateOnly(2026, 8, 6), typeId.Value, DocumentDirection.Internal,
                null, "Документ чужого подразделения", null, null, null, null, 0, 9, 42),
            [], useCommonDeadline: false, commonDeadline: null, new AccessContext("42", 10, [9]));

        var arguments = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["document"] = "неважно", ["author"] = "А.",
        };
        foreach (var documentId in new[] { open.DocumentId, secret.DocumentId, otherDivision.DocumentId })
        {
            (await notifications.RaiseAsync(new NotificationDraft(
                [11], NotificationType.CommentAdded, NotificationTemplates.CommentAdded, arguments, documentId)))
                .ShouldBe(1);
        }

        // Уведомление без документа — «своё» безусловно: скрывать нечего.
        (await notifications.RaiseAsync(new NotificationDraft(
            [11], NotificationType.CommentAdded, NotificationTemplates.CommentAdded, arguments)))
            .ShouldBe(1);

        // Допуск 0 и только подразделение 5: видно открытый документ и уведомление без документа.
        var limited = new AccessContext("11", 0, [5]);
        (await notifications.CountUnreadAsync(limited)).ShouldBe(2);
        var limitedFeed = await notifications.ListUnreadAsync(limited);
        limitedFeed.Select(n => n.DocumentId).ShouldBe(new int?[] { null, open.DocumentId }, ignoreOrder: true);

        // Тому же адресату с полным допуском видно всё — уведомления не удалялись, а скрывались.
        (await notifications.CountUnreadAsync(new AccessContext("11", 10, [5, 9]))).ShouldBe(4);

        // Fail-closed: без числового субъекта лента пуста, а не «всё подряд».
        (await notifications.CountUnreadAsync(new AccessContext("служебный", 10, [5]))).ShouldBe(0);
        (await notifications.ListUnreadAsync(new AccessContext("служебный", 10, [5]))).ShouldBeEmpty();
    }
}
