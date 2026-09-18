using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Investigation.Application.Features.Common;
using ISC.AI.Profile.Investigation.Application.Features.Roles;
using ISC.AI.Profile.Investigation.Domain.Enums;
using ISC.AI.Profile.Investigation.Domain.Services;
using NSubstitute;
using Shouldly;

namespace ISC.AI.UnitTests.Investigation;

/// <summary>
/// Ведение ролей (ТП-004): Администратор либо режим первичной настройки; последний Администратор неснимаем —
/// иначе окно первичной настройки открылось бы заново для любого вошедшего.
/// </summary>
public sealed class RoleScenarioTests
{
    private readonly IUserRoleStore _roles = Substitute.For<IUserRoleStore>();
    private readonly ISubjectProvider _subject = Substitute.For<ISubjectProvider>();

    public RoleScenarioTests()
    {
        _subject.GetCurrentUserIdAsync(Arg.Any<CancellationToken>()).Returns((int?)42);
        _roles.GetRoleAsync(42, Arg.Any<CancellationToken>()).Returns(InvestigationRole.Administrator);
        _roles.AnyAdministratorAsync(Arg.Any<CancellationToken>()).Returns(true);
        _roles.ListAsync(Arg.Any<CancellationToken>()).Returns([]);
    }

    [Fact(DisplayName = "Нельзя снять роль с последнего Администратора")]
    public async Task Cannot_remove_last_administrator()
    {
        _roles.ListUserIdsByRoleAsync(InvestigationRole.Administrator, Arg.Any<CancellationToken>()).Returns([42]);

        var response = await SetRoleAsync(42, null);

        response.Status.ShouldBeFalse();
        response.StatusMessage.ShouldBe(SetUserRoleCommand.LastAdministrator);
        await _roles.DidNotReceive().SetRoleAsync(Arg.Any<int>(), Arg.Any<InvestigationRole?>(), Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "Нельзя понизить последнего Администратора до другой роли")]
    public async Task Cannot_demote_last_administrator()
    {
        _roles.ListUserIdsByRoleAsync(InvestigationRole.Administrator, Arg.Any<CancellationToken>()).Returns([42]);

        (await SetRoleAsync(42, InvestigationRole.Head)).Status.ShouldBeFalse();
        await _roles.DidNotReceive().SetRoleAsync(Arg.Any<int>(), Arg.Any<InvestigationRole?>(), Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "При двух Администраторах роль с одного снимается")]
    public async Task Can_remove_administrator_when_another_exists()
    {
        _roles.GetRoleAsync(7, Arg.Any<CancellationToken>()).Returns(InvestigationRole.Administrator);
        _roles.ListUserIdsByRoleAsync(InvestigationRole.Administrator, Arg.Any<CancellationToken>()).Returns([42, 7]);

        (await SetRoleAsync(7, null)).Status.ShouldBeTrue();
        await _roles.Received(1).SetRoleAsync(7, null, Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "Назначение роли не-Администратору не касается правила последнего Администратора")]
    public async Task Assigning_role_to_non_administrator_skips_last_admin_check()
    {
        _roles.GetRoleAsync(7, Arg.Any<CancellationToken>()).Returns((InvestigationRole?)null);

        (await SetRoleAsync(7, InvestigationRole.Investigator)).Status.ShouldBeTrue();
        await _roles.DidNotReceive().ListUserIdsByRoleAsync(Arg.Any<InvestigationRole>(), Arg.Any<CancellationToken>());
        await _roles.Received(1).SetRoleAsync(7, InvestigationRole.Investigator, Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "Не-Администратор при наличии Администратора роли не назначает")]
    public async Task Non_administrator_is_denied()
    {
        _roles.GetRoleAsync(42, Arg.Any<CancellationToken>()).Returns(InvestigationRole.Head);

        var response = await SetRoleAsync(7, InvestigationRole.Investigator);

        response.Status.ShouldBeFalse();
        response.StatusMessage.ShouldBe(RoleGuard.AdminDenied);
    }

    [Fact(DisplayName = "Режим первичной настройки: любой вошедший назначает первого Администратора, реестр помечен флагом")]
    public async Task Bootstrap_allows_first_administrator_and_registry_flags_it()
    {
        _roles.GetRoleAsync(42, Arg.Any<CancellationToken>()).Returns((InvestigationRole?)null);
        _roles.AnyAdministratorAsync(Arg.Any<CancellationToken>()).Returns(false);

        var registry = await new ListUserRolesQuery.Handler(_roles, _subject)
            .Handle(new ListUserRolesQuery(), CancellationToken.None);
        registry.Status.ShouldBeTrue();
        registry.Data.ShouldNotBeNull().IsBootstrap.ShouldBeTrue();

        (await SetRoleAsync(42, InvestigationRole.Administrator)).Status.ShouldBeTrue();
    }

    [Fact(DisplayName = "При наличии Администратора флаг первичной настройки снят")]
    public async Task Registry_flag_is_off_when_administrator_exists()
    {
        var registry = await new ListUserRolesQuery.Handler(_roles, _subject)
            .Handle(new ListUserRolesQuery(), CancellationToken.None);

        registry.Data.ShouldNotBeNull().IsBootstrap.ShouldBeFalse();
    }

    private async Task<ISC.AI.Abstractions.Application.ResponseDto<bool>> SetRoleAsync(int userId, InvestigationRole? role) =>
        await new SetUserRoleCommand.Handler(_roles, _subject)
            .Handle(new SetUserRoleCommand(userId, role), CancellationToken.None);
}
