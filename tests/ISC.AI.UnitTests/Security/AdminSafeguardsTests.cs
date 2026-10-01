using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.DocFlow.Application.Features.Assignments;
using ISC.AI.Modules.DocFlow.Application.Features.Notifications;
using ISC.AI.Modules.DocFlow.Domain.Enums;
using ISC.AI.Modules.DocFlow.Domain.Services;
using ISC.AI.Profile.Inspector.Application.Features.Roles;
using ISC.AI.Profile.Inspector.Data;
using ISC.AI.Profile.Inspector.Domain.Enums;
using ISC.AI.Profile.Inspector.Domain.Services;
using NSubstitute;
using Shouldly;
using Xunit;

namespace ISC.AI.UnitTests.Security;

/// <summary>
/// Защитные правила администрирования (ТБ-012, 6.4.1, ТЗ СКИД §4.2): в «ИнспектореAI» нельзя снять последнего
/// действующего Администратора — ни сценарием профиля, ни с карточки сотрудника; снять поручение с контроля может
/// только тот, кому это право дал профиль (по умолчанию Руководитель), — проверяет сервер, а не меню.
/// </summary>
public sealed class AdminSafeguardsTests
{
    private const int Admin = 1;
    private const int Other = 2;

    private readonly IUserRoleStore _roles = Substitute.For<IUserRoleStore>();
    private readonly ISubjectProvider _subject = Substitute.For<ISubjectProvider>();

    public AdminSafeguardsTests()
    {
        _subject.GetCurrentUserIdAsync(Arg.Any<CancellationToken>()).Returns((int?)Admin);
        _roles.AnyAdministratorAsync(Arg.Any<CancellationToken>()).Returns(true);
        _roles.ListPermissionOverridesAsync(Arg.Any<CancellationToken>()).Returns([]);
        _roles.GetRoleAsync(Admin, Arg.Any<CancellationToken>()).Returns(UserRole.Administrator);
    }

    [Fact(DisplayName = "Инспектор: последнего Администратора снять нельзя — ни сценарием профиля, ни с карточки сотрудника")]
    public async Task Last_administrator_cannot_be_removed()
    {
        Staff(new UserRoleRow(Admin, "Админ", UserRole.Administrator), new UserRoleRow(Other, "Сотрудник", UserRole.Manager));

        var command = await new SetUserRoleCommand.Handler(_roles, _subject)
            .Handle(new SetUserRoleCommand(Admin, UserRole.Manager), CancellationToken.None);
        command.Status.ShouldBeFalse();
        command.StatusMessage.ShouldBe(RoleAssignmentRule.LastAdministrator);

        var card = await new InspectorUserRoleCatalog(_roles).AssignAsync(Admin, null);
        card.Succeeded.ShouldBeFalse();
        card.Error.ShouldBe(RoleAssignmentRule.LastAdministrator);

        await _roles.DidNotReceiveWithAnyArgs().SetRoleAsync(default, default);
    }

    [Fact(DisplayName = "Инспектор: Администратор с отключённой учётной записью не считается — снять последнего действующего нельзя")]
    public async Task Inactive_administrator_does_not_count()
    {
        // ListAsync возвращает только действующие записи: отключённого Администратора в нём нет.
        Staff(new UserRoleRow(Admin, "Админ", UserRole.Administrator));
        _roles.GetRoleAsync(3, Arg.Any<CancellationToken>()).Returns(UserRole.Administrator);

        (await RoleAssignmentRule.CheckAsync(_roles, Admin, null)).ShouldBe(RoleAssignmentRule.LastAdministrator);
    }

    [Fact(DisplayName = "Инспектор: при втором Администраторе роль снимается; назначение Администратора и смена роли не-Администратору не ограничены")]
    public async Task Removal_allowed_when_another_administrator_exists()
    {
        Staff(new UserRoleRow(Admin, "Админ", UserRole.Administrator), new UserRoleRow(Other, "Второй", UserRole.Administrator));
        _roles.GetRoleAsync(Other, Arg.Any<CancellationToken>()).Returns(UserRole.Administrator);

        (await new SetUserRoleCommand.Handler(_roles, _subject)
            .Handle(new SetUserRoleCommand(Admin, UserRole.Manager), CancellationToken.None)).Status.ShouldBeTrue();
        await _roles.Received(1).SetRoleAsync(Admin, UserRole.Manager, Arg.Any<CancellationToken>());

        (await RoleAssignmentRule.CheckAsync(_roles, Admin, UserRole.Administrator)).ShouldBeNull();
        _roles.GetRoleAsync(5, Arg.Any<CancellationToken>()).Returns(UserRole.Inspector);
        (await RoleAssignmentRule.CheckAsync(_roles, 5, null)).ShouldBeNull();
    }

    [Fact(DisplayName = "Документооборот: без права снять с контроля — отказ до обращения к хранилищу")]
    public async Task Closing_assignment_requires_right()
    {
        var (handler, store, administration) = StatusHandler();
        administration.CanCloseAssignmentsAsync(Arg.Any<CancellationToken>()).Returns(false);

        var response = await handler.Handle(
            new ChangeAssignmentStatusCommand(7, AssignmentStatus.Closed, null), CancellationToken.None);

        response.Status.ShouldBeFalse();
        response.StatusMessage.ShouldBe(ChangeAssignmentStatusCommand.CloseDenied);
        await store.DidNotReceiveWithAnyArgs().ChangeAssignmentStatusAsync(default, default, default, default!, default, default);
    }

    [Fact(DisplayName = "Документооборот: с правом снятие проходит; другие переходы право на снятие не спрашивают")]
    public async Task Closing_with_right_and_other_transitions()
    {
        var (handler, store, administration) = StatusHandler();
        administration.CanCloseAssignmentsAsync(Arg.Any<CancellationToken>()).Returns(true);

        (await handler.Handle(new ChangeAssignmentStatusCommand(7, AssignmentStatus.Closed, null), CancellationToken.None))
            .Status.ShouldBeTrue();

        administration.ClearReceivedCalls();
        (await handler.Handle(new ChangeAssignmentStatusCommand(7, AssignmentStatus.InProgress, null), CancellationToken.None))
            .Status.ShouldBeTrue();
        await administration.DidNotReceiveWithAnyArgs().CanCloseAssignmentsAsync(default);
        await store.Received(2).ChangeAssignmentStatusAsync(
            7, Arg.Any<AssignmentStatus>(), null, Arg.Any<AccessContext>(), null, Arg.Any<CancellationToken>());
    }

    [Theory(DisplayName = "Инспектор: снять с контроля по умолчанию может только Руководитель")]
    [InlineData(UserRole.Manager, true)]
    [InlineData(UserRole.Administrator, false)]
    [InlineData(UserRole.Inspector, false)]
    [InlineData(UserRole.Performer, false)]
    public async Task Inspector_close_right_defaults_to_manager(UserRole role, bool expected)
    {
        _roles.GetRoleAsync(Admin, Arg.Any<CancellationToken>()).Returns(role);

        (await new DocFlowAdministration(_roles, _subject).CanCloseAssignmentsAsync()).ShouldBe(expected);
    }

    private void Staff(params UserRoleRow[] rows) =>
        _roles.ListAsync(Arg.Any<CancellationToken>()).Returns((IReadOnlyList<UserRoleRow>)rows);

    private static (ChangeAssignmentStatusCommand.Handler Handler, IDocumentStore Store, IDocFlowAdministration Administration) StatusHandler()
    {
        var store = Substitute.For<IDocumentStore>();
        var access = Substitute.For<IAccessContextProvider>();
        var administration = Substitute.For<IDocFlowAdministration>();
        access.GetCurrentAsync(Arg.Any<CancellationToken>()).Returns(new AccessContext("42", 2, [5]));
        store.ChangeAssignmentStatusAsync(default, default, default, default!, default, default)
            .ReturnsForAnyArgs(DocumentWriteStatus.Ok);
        store.GetAssignmentParticipantsAsync(default, default!, default).ReturnsForAnyArgs((AssignmentParticipants?)null);

        var notifier = new DocFlowEventNotifier(Substitute.For<INotificationStore>(), Substitute.For<IUserDirectory>());
        return (new ChangeAssignmentStatusCommand.Handler(store, access, notifier, administration), store, administration);
    }
}
