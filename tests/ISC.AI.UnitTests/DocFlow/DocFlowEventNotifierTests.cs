using ISC.AI.Modules.DocFlow.Application.Features.Notifications;
using ISC.AI.Modules.DocFlow.Domain.Enums;
using ISC.AI.Modules.DocFlow.Domain.Services;
using NSubstitute;
using Shouldly;

namespace ISC.AI.UnitTests.DocFlow;

/// <summary>
/// Правила адресации уведомлений (разд. 5 ТЗ СКИД, этап 2.2b Э4-35): инициатор себя не уведомляет,
/// один человек по одному событию получает не больше одного уведомления, сбой рассылки не отменяет
/// уже совершённое действие.
/// </summary>
public sealed class DocFlowEventNotifierTests
{
    private readonly INotificationStore _notifications = Substitute.For<INotificationStore>();
    private readonly IUserDirectory _users = Substitute.For<IUserDirectory>();

    public DocFlowEventNotifierTests() =>
        _users.GetNameAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns("Иванов И.И.");

    [Fact(DisplayName = "Комментарий: упомянутому — «упомянули», участникам — «добавлен», автору — ничего")]
    public async Task Comment_notifies_mentioned_once_and_never_the_author()
    {
        var notifier = new DocFlowEventNotifier(_notifications, _users);

        // Автор (1) он же исполнитель одного назначения; упомянутый (2) — исполнитель другого;
        // инспектор (3) не упомянут. Ожидание: 2 получает ТОЛЬКО упоминание, 3 — «добавлен», 1 — ничего.
        var document = Document(inspectorUserId: 3, assignees: [1, 2]);
        await notifier.CommentAddedAsync(document, commentId: 77, authorUserId: 1, mentionedUserIds: [1, 2]);

        var drafts = CapturedDrafts();

        var mention = drafts.Where(d => d.Type == NotificationType.MentionedInComment).ShouldHaveSingleItem();
        mention.RecipientUserIds.ShouldBe(new[] { 2 });
        mention.CommentId.ShouldBe(77);

        var added = drafts.Where(d => d.Type == NotificationType.CommentAdded).ShouldHaveSingleItem();
        added.RecipientUserIds.ShouldBe(new[] { 3 });
    }

    [Fact(DisplayName = "Смена статуса: исполнитель, инспектор и контролёр — по одному разу, кроме инициатора")]
    public async Task Status_change_notifies_participants_without_duplicates()
    {
        var notifier = new DocFlowEventNotifier(_notifications, _users);

        // Инспектор (3) одновременно контролёр — дубля быть не должно; инициатор (1) исключён.
        var participants = new AssignmentParticipants(
            AssignmentId: 5, DocumentId: 9, DocumentTitle: "П-1", Deadline: new DateOnly(2026, 8, 20),
            AssigneeUserId: 1, InspectorUserId: 3, ControllerUserId: 3);

        await notifier.AssignmentStatusChangedAsync(participants, AssignmentStatus.Done, actorUserId: 1);

        var draft = CapturedDrafts().ShouldHaveSingleItem();
        draft.RecipientUserIds.ShouldBe(new[] { 3 });
        draft.Arguments["status"].ShouldBe("Исполнено");
        draft.Arguments["actor"].ShouldBe("Иванов И.И.");
    }

    [Fact(DisplayName = "Продление: в тексте старый и новый сроки, инициатор не уведомляется")]
    public async Task Extension_carries_both_deadlines()
    {
        var notifier = new DocFlowEventNotifier(_notifications, _users);

        var participants = new AssignmentParticipants(
            AssignmentId: 5, DocumentId: 9, DocumentTitle: "П-1", Deadline: new DateOnly(2026, 8, 20),
            AssigneeUserId: 4, InspectorUserId: 1, ControllerUserId: null);

        await notifier.DeadlineExtendedAsync(participants, new DateOnly(2026, 9, 1), actorUserId: 1);

        var draft = CapturedDrafts().ShouldHaveSingleItem();
        draft.RecipientUserIds.ShouldBe(new[] { 4 });
        draft.Arguments["old"].ShouldBe("20.08.2026");
        draft.Arguments["new"].ShouldBe("01.09.2026");
    }

    [Fact(DisplayName = "Регистрация: исполнителю — «вам назначено», инспектору — «создано назначение»")]
    public async Task Registration_notifies_assignee_and_inspector_differently()
    {
        var notifier = new DocFlowEventNotifier(_notifications, _users);

        var created = Created(inspectorUserId: 3, assignees: [2]);
        await notifier.DocumentRegisteredAsync(created, actorUserId: 1);

        var drafts = CapturedDrafts();
        drafts.Count.ShouldBe(2);
        drafts.ShouldContain(d => d.MessageKey == NotificationTemplates.AssignedToYou
            && d.RecipientUserIds.SequenceEqual(new[] { 2 }));
        drafts.ShouldContain(d => d.MessageKey == NotificationTemplates.AssignedNotice
            && d.RecipientUserIds.SequenceEqual(new[] { 3 }));
    }

    [Fact(DisplayName = "Регистрация: инспектор, он же исполнитель, получает только «вам назначено»")]
    public async Task Inspector_who_is_also_assignee_is_not_notified_twice()
    {
        var notifier = new DocFlowEventNotifier(_notifications, _users);

        var created = Created(inspectorUserId: 2, assignees: [2]);
        await notifier.DocumentRegisteredAsync(created, actorUserId: 1);

        var draft = CapturedDrafts().ShouldHaveSingleItem();
        draft.MessageKey.ShouldBe(NotificationTemplates.AssignedToYou);
        draft.RecipientUserIds.ShouldBe(new[] { 2 });
    }

    [Fact(DisplayName = "Сбой рассылки не роняет сценарий: SafeAsync возвращает признак, а не исключение")]
    public async Task Failed_delivery_is_reported_not_thrown()
    {
        (await DocFlowEventNotifier.SafeAsync(() => throw new InvalidOperationException("БД недоступна")))
            .ShouldBeFalse();
        (await DocFlowEventNotifier.SafeAsync(() => Task.CompletedTask)).ShouldBeTrue();
    }

    [Fact(DisplayName = "Отмена не глотается SafeAsync — остановка приложения должна дойти до вызывающего")]
    public async Task Cancellation_is_not_swallowed() =>
        await Should.ThrowAsync<OperationCanceledException>(
            () => DocFlowEventNotifier.SafeAsync(() => throw new OperationCanceledException()));

    /// <summary>
    /// Итог регистрации для уведомлений — приходит ИЗ операции создания, а не перечитыванием карточки
    /// (иначе уведомления пропадали бы для документов, невидимых самому регистратору).
    /// </summary>
    private static CreatedDocumentNotice Created(int inspectorUserId, IReadOnlyList<int> assignees) =>
        new(
            DocumentId: 9,
            DocumentTitle: "П-1",
            InspectorUserId: inspectorUserId,
            Assignments:
            [
                .. assignees.Select((assignee, index) => new CreatedAssignmentNotice(
                    AssignmentId: index + 1, AssigneeUserId: assignee, Deadline: new DateOnly(2026, 8, 20))),
            ]);

    private static DocumentDetails Document(int inspectorUserId, IReadOnlyList<int> assignees) =>
        new(
            Id: 9,
            RegNumber: "П-1",
            RegDate: new DateOnly(2026, 8, 6),
            TypeId: 1,
            TypeName: "Поручение",
            Group: DocumentGroup.Execution,
            Direction: DocumentDirection.Incoming,
            Source: null,
            ShortContent: "Содержание",
            FullText: null,
            Notes: null,
            Priority: DocumentPriority.Medium,
            InspectorUserId: inspectorUserId,
            AggregatedStatus: DocumentAggregatedStatus.InProgress,
            Classification: 0,
            DivisionId: 5,
            Assignments:
            [
                .. assignees.Select((assignee, index) => new AssignmentDetails(
                    Id: index + 1, DivisionId: 5, AssigneeUserId: assignee,
                    Status: AssignmentStatus.Registered, Deadline: new DateOnly(2026, 8, 20),
                    ControllerUserId: null)),
            ],
            IndexedAt: null,
            Files: [],
            Attachments: []);

    /// <summary>Черновики, реально ушедшие в хранилище: проверяется адресация, а не факт вызова.</summary>
    private IReadOnlyList<NotificationDraft> CapturedDrafts() =>
    [
        .. _notifications.ReceivedCalls()
            .Where(call => call.GetMethodInfo().Name == nameof(INotificationStore.RaiseAsync))
            .Select(call => (NotificationDraft)call.GetArguments()[0]!),
    ];
}
