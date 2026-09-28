using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Investigation.Application.Features.Common;
using ISC.AI.Profile.Investigation.Application.Features.References;
using ISC.AI.Profile.Investigation.Domain.Enums;
using ISC.AI.Profile.Investigation.Domain.Services;
using NSubstitute;
using Shouldly;

namespace ISC.AI.UnitTests.Investigation;

/// <summary>
/// Справочники профиля (ТФ-АДМ-07): правит только Администратор (ТП-004), читает любой вошедший;
/// занятое наименование — конфликт, отсутствующая запись — «не найдено».
/// </summary>
public sealed class ReferenceGuardTests
{
    private readonly IReferenceStore _store = Substitute.For<IReferenceStore>();
    private readonly IUserRoleStore _roles = Substitute.For<IUserRoleStore>();
    private readonly ISubjectProvider _subject = Substitute.For<ISubjectProvider>();

    public ReferenceGuardTests()
    {
        _subject.GetCurrentUserIdAsync(Arg.Any<CancellationToken>()).Returns((int?)42);
        _roles.AnyAdministratorAsync(Arg.Any<CancellationToken>()).Returns(true);
        _store.CreateAsync(Arg.Any<ReferenceKind>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns((ReferenceWriteResult.Ok, 11));
        _store.UpdateAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(ReferenceWriteResult.Ok);
        _store.SetActiveAsync(Arg.Any<int>(), Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(ReferenceWriteResult.Ok);
    }

    [Theory(DisplayName = "Не-Администратор при наличии Администратора: создание, правка и выключение отклоняются, хранилище не трогается")]
    [InlineData(InvestigationRole.Investigator)]
    [InlineData(InvestigationRole.Head)]
    [InlineData(InvestigationRole.SecurityOfficer)]
    public async Task Non_administrator_cannot_write(InvestigationRole role)
    {
        _roles.GetRoleAsync(42, Arg.Any<CancellationToken>()).Returns(role);

        var create = await new CreateReferenceItemCommand.Handler(_store, _roles, _subject)
            .Handle(new CreateReferenceItemCommand(ReferenceKind.InitiatorUnit, "ГУ-1"), CancellationToken.None);
        var update = await new UpdateReferenceItemCommand.Handler(_store, _roles, _subject)
            .Handle(new UpdateReferenceItemCommand(3, "ГУ-2"), CancellationToken.None);
        var disable = await new SetReferenceItemActiveCommand.Handler(_store, _roles, _subject)
            .Handle(new SetReferenceItemActiveCommand(3, false), CancellationToken.None);

        create.StatusMessage.ShouldBe(RoleGuard.AdminDenied);
        update.Status.ShouldBeFalse();
        disable.Status.ShouldBeFalse();
        await _store.DidNotReceiveWithAnyArgs().CreateAsync(default, default!, default, default, default);
        await _store.DidNotReceiveWithAnyArgs().UpdateAsync(default, default!, default, default, default);
        await _store.DidNotReceiveWithAnyArgs().SetActiveAsync(default, default, default);
    }

    [Fact(DisplayName = "Администратор создаёт запись; наименование и код обрезаются по краям")]
    public async Task Administrator_creates_trimmed_item()
    {
        _roles.GetRoleAsync(42, Arg.Any<CancellationToken>()).Returns(InvestigationRole.Administrator);

        var response = await new CreateReferenceItemCommand.Handler(_store, _roles, _subject)
            .Handle(new CreateReferenceItemCommand(ReferenceKind.Rank, "  майор ", "  ", 20), CancellationToken.None);

        response.Status.ShouldBeTrue();
        response.Data.ShouldBe(11);
        await _store.Received(1).CreateAsync(ReferenceKind.Rank, "майор", null, 20, Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "Занятое наименование → Conflict; отсутствующая запись → NotFound")]
    public async Task Duplicate_is_conflict_and_missing_is_not_found()
    {
        _roles.GetRoleAsync(42, Arg.Any<CancellationToken>()).Returns(InvestigationRole.Administrator);
        _store.CreateAsync(Arg.Any<ReferenceKind>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns((ReferenceWriteResult.Duplicate, 0));
        _store.UpdateAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(ReferenceWriteResult.NotFound);

        var create = await new CreateReferenceItemCommand.Handler(_store, _roles, _subject)
            .Handle(new CreateReferenceItemCommand(ReferenceKind.Rank, "майор"), CancellationToken.None);
        var update = await new UpdateReferenceItemCommand.Handler(_store, _roles, _subject)
            .Handle(new UpdateReferenceItemCommand(99, "майор"), CancellationToken.None);

        create.StatusCode.ShouldBe(ResponseStatusCode.Conflict);
        create.StatusMessage.ShouldBe(ReferenceGuard.Duplicate);
        update.StatusCode.ShouldBe(ResponseStatusCode.NotFound);
    }

    [Fact(DisplayName = "Чтение: вошедшему без роли — список; без аутентификации — отказ, хранилище не читается")]
    public async Task List_requires_authentication_only()
    {
        _store.ListAsync(Arg.Any<ReferenceKind?>(), Arg.Any<CancellationToken>())
            .Returns([new ReferenceItemRow(1, ReferenceKind.InitiatorUnit, "ГУ-1", null, 0, true)]);
        _roles.GetRoleAsync(42, Arg.Any<CancellationToken>()).Returns((InvestigationRole?)null);

        var handler = new ListReferenceItemsQuery.Handler(_store, _subject);
        (await handler.Handle(new ListReferenceItemsQuery(ReferenceKind.InitiatorUnit), CancellationToken.None))
            .Data.ShouldNotBeNull().ShouldHaveSingleItem();

        _subject.GetCurrentUserIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);
        _store.ClearReceivedCalls();
        (await handler.Handle(new ListReferenceItemsQuery(), CancellationToken.None)).Status.ShouldBeFalse();
        await _store.DidNotReceiveWithAnyArgs().ListAsync(default, default);
    }

    [Fact(DisplayName = "Аудит справочника: вид и наименование в сводке, выключение — «disable»")]
    public void Audit_summaries_name_the_change()
    {
        new CreateReferenceItemCommand(ReferenceKind.InitiatorUnit, "ГУ-1").AuditSummary.ShouldBe("investigation:reference:create:kind=InitiatorUnit:ГУ-1");
        new SetReferenceItemActiveCommand(5, false).AuditSummary.ShouldBe("investigation:reference:5:disable");
    }

    [Fact(DisplayName = "Аудит справочника: наименование обрезано, переводы строк заменены, длина не больше предела — сводка пишется и для отклонённой команды")]
    public void Audit_name_is_sanitized()
    {
        var forged = new UpdateReferenceItemCommand(3, "  ГУ-1\ninvestigation:case:1:purge  ").AuditSummary.ShouldNotBeNull();
        forged.ShouldBe("investigation:reference:3:update:ГУ-1 investigation:case:1:purge");
        forged.ShouldNotContain("\n");

        var huge = new CreateReferenceItemCommand(ReferenceKind.Rank, new string('я', 10_000)).AuditSummary.ShouldNotBeNull();
        huge.Length.ShouldBeLessThanOrEqualTo("investigation:reference:create:kind=Rank:".Length + ReferenceGuard.MaxNameLength);
        ReferenceGuard.AuditName(null).ShouldBe(string.Empty);
    }
}
