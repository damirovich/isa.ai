using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ISC.AI.Profile.Investigation.Data;
using ISC.AI.Profile.Investigation.Domain.Enums;
using ISC.AI.Profile.Investigation.Domain.Services;
using NSubstitute;
using Shouldly;
using Xunit;

namespace ISC.AI.UnitTests.Investigation;

/// <summary>
/// Назначение роли из карточки сотрудника пакета администрирования через порт профиля «Следствие» (ТП-004/007):
/// ключ разбирается строго, роль получает только действующий сотрудник, последнего Администратора снять нельзя —
/// тем же правилом, что и в сценарии профиля.
/// </summary>
public sealed class InvestigationRoleAssignmentTests
{
    private readonly IUserRoleStore _roles = Substitute.For<IUserRoleStore>();

    public InvestigationRoleAssignmentTests()
    {
        _roles.ListAsync(Arg.Any<CancellationToken>()).Returns(new List<UserRoleRow>
        {
            new(1, "Администратор", InvestigationRole.Administrator),
            new(2, "Ашыров", InvestigationRole.Verifier),
        });
        _roles.GetRoleAsync(1, Arg.Any<CancellationToken>()).Returns(InvestigationRole.Administrator);
        _roles.GetRoleAsync(2, Arg.Any<CancellationToken>()).Returns(InvestigationRole.Verifier);
        _roles.ListUserIdsByRoleAsync(InvestigationRole.Administrator, Arg.Any<CancellationToken>()).Returns([1]);
    }

    [Theory(DisplayName = "Неизвестный или «почти правильный» ключ роли — отказ, роль не меняется")]
    [InlineData("Boss")]
    [InlineData("administrator")]
    [InlineData("1")]
    public async Task Unknown_key_is_rejected(string key)
    {
        var result = await new InvestigationUserRoleCatalog(_roles).AssignAsync(2, key);

        result.Succeeded.ShouldBeFalse();
        result.Error.ShouldBe("Неизвестная роль.");
        await _roles.DidNotReceive().SetRoleAsync(Arg.Any<int>(), Arg.Any<InvestigationRole?>(), Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "Отключённому или несуществующему сотруднику роль не назначается")]
    public async Task Inactive_user_is_rejected()
    {
        var result = await new InvestigationUserRoleCatalog(_roles).AssignAsync(99, "Verifier");

        result.Succeeded.ShouldBeFalse();
        await _roles.DidNotReceive().SetRoleAsync(Arg.Any<int>(), Arg.Any<InvestigationRole?>(), Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "Последнего Администратора снять нельзя — тот же текст, что у сценария профиля")]
    public async Task Last_administrator_is_protected()
    {
        var result = await new InvestigationUserRoleCatalog(_roles).AssignAsync(1, "Verifier");

        result.Succeeded.ShouldBeFalse();
        result.Error.ShouldBe(RoleAssignmentRule.LastAdministrator);
    }

    [Fact(DisplayName = "Обычное назначение и снятие роли проходят")]
    public async Task Assign_and_remove()
    {
        var catalog = new InvestigationUserRoleCatalog(_roles);

        (await catalog.AssignAsync(2, "Administrator")).Succeeded.ShouldBeTrue();
        await _roles.Received(1).SetRoleAsync(2, InvestigationRole.Administrator, Arg.Any<CancellationToken>());

        (await catalog.AssignAsync(2, null)).Succeeded.ShouldBeTrue();
        await _roles.Received(1).SetRoleAsync(2, null, Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "У каждой роли профиля есть описание для карточки сотрудника")]
    public async Task Every_role_has_description()
    {
        var options = await new InvestigationUserRoleCatalog(_roles).ListRolesAsync();

        options.ShouldAllBe(o => !string.IsNullOrWhiteSpace(o.Description) && o.Description != o.Label);
    }
}
