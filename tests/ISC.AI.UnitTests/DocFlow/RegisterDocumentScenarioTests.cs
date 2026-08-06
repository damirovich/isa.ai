using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.DocFlow.Application.Documents;
using ISC.AI.Modules.DocFlow.Application.Notifications;
using ISC.AI.Modules.DocFlow.Domain.Enums;
using ISC.AI.Modules.DocFlow.Domain.Services;
using NSubstitute;
using Shouldly;

namespace ISC.AI.UnitTests.DocFlow;

/// <summary>
/// Сценарий регистрации документа: сопоставление статуса хранилища с тем, что РЕАЛЬНО ВИДИТ
/// пользователь, и рассылка уведомлений о созданных назначениях (§4.1, разд. 5).
/// </summary>
/// <remarks>
/// Раздельные отказы по грифу и по подразделению проверяются здесь, а не только на уровне хранилища
/// (<c>InspectorAccessPolicyTests</c>): смысл изменения — в РАЗНЫХ ТЕКСТАХ, а тексты живут в сценарии.
/// Тест на уровне хранилища прошёл бы и с одним общим сообщением.
/// </remarks>
public sealed class RegisterDocumentScenarioTests
{
    private readonly IDocumentStore _store = Substitute.For<IDocumentStore>();
    private readonly INotificationStore _notifications = Substitute.For<INotificationStore>();
    private readonly IUserDirectory _users = Substitute.For<IUserDirectory>();

    [Fact(DisplayName = "Отказ по грифу называет ИМЕННО гриф и не поминает подразделение")]
    public async Task Classification_refusal_names_the_classification_field()
    {
        var response = await HandleAsync(
            new DocumentCreateResult(DocumentWriteStatus.ClassificationOutsideClearance));

        response.Status.ShouldBeFalse();
        response.StatusMessage.ShouldContain("Гриф");
        response.StatusMessage.ShouldNotContain("Подразделение");
    }

    [Fact(DisplayName = "Отказ по подразделению называет ИМЕННО подразделение и не поминает гриф")]
    public async Task Division_refusal_names_the_division_field()
    {
        var response = await HandleAsync(
            new DocumentCreateResult(DocumentWriteStatus.DivisionOutsideClearance));

        response.Status.ShouldBeFalse();
        response.StatusMessage.ShouldContain("Подразделение");
        response.StatusMessage.ShouldNotContain("Гриф");
    }

    [Fact(DisplayName = "Два отказа по допуску дают РАЗНЫЕ сообщения — иначе поле не угадать")]
    public async Task Two_clearance_refusals_differ()
    {
        var byClassification = await HandleAsync(
            new DocumentCreateResult(DocumentWriteStatus.ClassificationOutsideClearance));
        var byDivision = await HandleAsync(
            new DocumentCreateResult(DocumentWriteStatus.DivisionOutsideClearance));

        byClassification.StatusMessage.ShouldNotBe(byDivision.StatusMessage);
    }

    [Fact(DisplayName = "Уведомления уходят исполнителю, даже если сам регистратор свой документ не видит")]
    public async Task Assignment_notifications_do_not_depend_on_author_visibility()
    {
        var notice = new CreatedDocumentNotice(
            DocumentId: 9,
            DocumentTitle: "П-1",
            InspectorUserId: 3,
            Assignments: [new CreatedAssignmentNotice(AssignmentId: 1, AssigneeUserId: 2, Deadline: null)]);

        // Ключевой момент: карточка НЕДОСТУПНА автору (политика профиля по роли вернула бы null).
        // Раньше уведомления читались именно через GetAsync и в этом случае молча исчезали.
        _store.GetAsync(Arg.Any<int>(), Arg.Any<AccessContext>(), Arg.Any<CancellationToken>())
            .Returns((DocumentDetails?)null);

        var response = await HandleAsync(new DocumentCreateResult(DocumentWriteStatus.Ok, 9, notice));

        response.Status.ShouldBeTrue();

        var drafts = _notifications.ReceivedCalls()
            .Where(call => call.GetMethodInfo().Name == nameof(INotificationStore.RaiseAsync))
            .Select(call => (NotificationDraft)call.GetArguments()[0]!)
            .ToList();

        drafts.ShouldContain(d => d.MessageKey == NotificationTemplates.AssignedToYou
            && d.RecipientUserIds.SequenceEqual(new[] { 2 }));
        drafts.ShouldContain(d => d.MessageKey == NotificationTemplates.AssignedNotice
            && d.RecipientUserIds.SequenceEqual(new[] { 3 }));
    }

    private async Task<ISC.AI.Abstractions.Application.ResponseDto<int>> HandleAsync(DocumentCreateResult result)
    {
        _store.CreateAsync(
            Arg.Any<DocumentDraft>(), Arg.Any<IReadOnlyList<AssignmentDraft>>(), Arg.Any<bool>(),
            Arg.Any<DateOnly?>(), Arg.Any<AccessContext>(), Arg.Any<CancellationToken>())
            .Returns(result);

        var accessProvider = Substitute.For<IAccessContextProvider>();
        accessProvider.GetCurrentAsync(Arg.Any<CancellationToken>())
            .Returns(new AccessContext("42", MaxClassification: 1, AllowedDivisions: [5]));

        var handler = new RegisterDocumentCommand.Handler(
            _store, accessProvider, new NoopTaskQueue(), new DocFlowEventNotifier(_notifications, _users));

        return await handler.Handle(
            new RegisterDocumentCommand(
                "П-1", new DateOnly(2026, 8, 6), 1, DocumentDirection.Incoming, null, "Содержание",
                null, null, DocumentPriority.Medium, 3, 0, 5, [], UseCommonDeadline: false, CommonDeadline: null),
            CancellationToken.None);
    }

    /// <summary>Очередь фоновых задач в этих проверках не участвует — индексация здесь не предмет.</summary>
    private sealed class NoopTaskQueue : ISC.AI.Abstractions.BackgroundTasks.IBackgroundTaskQueue
    {
        public ValueTask<Guid> EnqueueAsync(
            string kind,
            Func<IServiceProvider, CancellationToken, Task> work,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Guid.Empty);
    }
}
