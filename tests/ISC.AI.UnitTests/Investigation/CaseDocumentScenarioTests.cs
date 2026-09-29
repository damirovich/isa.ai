using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.DocFlow.Domain.Enums;
using ISC.AI.Modules.DocFlow.Domain.Services;
using ISC.AI.Profile.Investigation.Application.Features.CaseDocuments;
using ISC.AI.Profile.Investigation.Application.Features.Common;
using ISC.AI.Profile.Investigation.Domain.Enums;
using ISC.AI.Profile.Investigation.Domain.Services;
using Mediator;
using NSubstitute;
using Shouldly;

namespace ISC.AI.UnitTests.Investigation;

/// <summary>
/// Документы дела (ТФ-ДЕЛ-02) без БД: список не показывает документы вне допуска документооборота; прикрепить
/// можно только видимый документ и только ролью, ведущей дела; регистрация сводки — та же охрана.
/// </summary>
public sealed class CaseDocumentScenarioTests
{
    private readonly ICaseDocumentStore _store = Substitute.For<ICaseDocumentStore>();
    private readonly IDocumentLookup _documents = Substitute.For<IDocumentLookup>();
    private readonly IUserRoleStore _roles = Substitute.For<IUserRoleStore>();
    private readonly ISubjectProvider _subject = Substitute.For<ISubjectProvider>();
    private readonly IAccessContextProvider _access = Substitute.For<IAccessContextProvider>();

    public CaseDocumentScenarioTests()
    {
        _subject.GetCurrentUserIdAsync(Arg.Any<CancellationToken>()).Returns((int?)42);
        _roles.GetRoleAsync(42, Arg.Any<CancellationToken>()).Returns(InvestigationRole.Investigator);
        _access.GetCurrentAsync(Arg.Any<CancellationToken>()).Returns(new AccessContext("42", 2, [5]));
    }

    [Fact(DisplayName = "Список: документ вне допуска документооборота не показывается ни строкой, ни в итоге")]
    public async Task List_hides_invisible_documents()
    {
        _store.ListAsync(3, Arg.Any<AccessContext>(), Arg.Any<CancellationToken>())
            .Returns([new CaseDocumentLinkRow(101, 42, DateTime.UtcNow), new CaseDocumentLinkRow(102, 42, DateTime.UtcNow)]);
        _documents.ResolveByIdsAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<AccessContext>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<int, DocumentBrief> { [101] = Brief(101) });

        var response = await new ListCaseDocumentsQuery.Handler(_store, _documents, _access)
            .Handle(new ListCaseDocumentsQuery(3), CancellationToken.None);

        response.Data.ShouldNotBeNull().ShouldHaveSingleItem().Document.Id.ShouldBe(101);
        response.TotalCount.ShouldBe(1);
    }

    [Fact(DisplayName = "Прикрепить: невидимый документ — «не найден» без записи; роль без права — отказ")]
    public async Task Attach_requires_visible_document_and_editor_role()
    {
        _documents.ResolveByIdsAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<AccessContext>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<int, DocumentBrief>());
        var handler = new AttachCaseDocumentCommand.Handler(_store, _documents, _roles, _subject, _access);

        (await handler.Handle(new AttachCaseDocumentCommand(3, 101), CancellationToken.None)).StatusCode.ShouldBe(ResponseStatusCode.NotFound);
        await _store.DidNotReceiveWithAnyArgs().AttachAsync(default, default, default!, default);

        _roles.GetRoleAsync(42, Arg.Any<CancellationToken>()).Returns(InvestigationRole.Verifier);
        (await handler.Handle(new AttachCaseDocumentCommand(3, 101), CancellationToken.None)).StatusMessage.ShouldBe(RoleGuard.CaseDenied);
    }

    [Fact(DisplayName = "Прикрепить видимый документ — успех; повтор — понятный отказ; регистрация сводки без права — отказ")]
    public async Task Attach_visible_document_and_register_guard()
    {
        _documents.ResolveByIdsAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<AccessContext>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<int, DocumentBrief> { [101] = Brief(101) });
        _store.AttachAsync(3, 101, Arg.Any<AccessContext>(), Arg.Any<CancellationToken>())
            .Returns(CaseDocumentWriteResult.Ok, CaseDocumentWriteResult.AlreadyLinked);
        var handler = new AttachCaseDocumentCommand.Handler(_store, _documents, _roles, _subject, _access);

        (await handler.Handle(new AttachCaseDocumentCommand(3, 101), CancellationToken.None)).Status.ShouldBeTrue();
        (await handler.Handle(new AttachCaseDocumentCommand(3, 101), CancellationToken.None)).StatusMessage.ShouldContain("уже прикреплён");

        _roles.GetRoleAsync(42, Arg.Any<CancellationToken>()).Returns(InvestigationRole.FaceExpert);
        var register = await new RegisterCaseReportDocumentCommand.Handler(
                Substitute.For<IMediator>(), Substitute.For<ICaseReportStore>(), Substitute.For<ICaseStore>(), _store, _roles, _subject, _access)
            .Handle(new RegisterCaseReportDocumentCommand(9, 1), CancellationToken.None);
        register.StatusMessage.ShouldBe(RoleGuard.CaseDenied);
    }

    private static DocumentBrief Brief(int id) =>
        new(id, $"ВН-{id}", new DateOnly(2026, 9, 29), "Справка", "Краткое содержание", 42, DocumentAggregatedStatus.Registered, 2);
}
