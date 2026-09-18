using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Investigation.Application.Features.Common;
using ISC.AI.Profile.Investigation.Application.Features.Divisions;
using ISC.AI.Profile.Investigation.Domain.Enums;
using ISC.AI.Profile.Investigation.Domain.Services;
using NSubstitute;
using Shouldly;

namespace ISC.AI.UnitTests.Investigation;

/// <summary>
/// Справочник подразделений — словарь решётки доступа (ТБ-020): правит только Администратор (ТФ-АДМ-01, ТП-004).
/// У «Инспектора» этого гварда нет — здесь он обязателен и проверяется на всех трёх командах записи.
/// </summary>
public sealed class DivisionGuardTests
{
    private readonly IDivisionAdminStore _store = Substitute.For<IDivisionAdminStore>();
    private readonly IUserRoleStore _roles = Substitute.For<IUserRoleStore>();
    private readonly ISubjectProvider _subject = Substitute.For<ISubjectProvider>();

    public DivisionGuardTests()
    {
        _subject.GetCurrentUserIdAsync(Arg.Any<CancellationToken>()).Returns((int?)42);
        _store.CreateAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<int?>(), Arg.Any<CancellationToken>()).Returns(11);
        _store.RenameAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(DivisionWriteResult.Ok);
        _store.SetActiveAsync(Arg.Any<int>(), Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(DivisionWriteResult.Ok);
    }

    [Fact(DisplayName = "Не-Администратор при наличии Администратора: создание отклоняется, хранилище не трогается")]
    public async Task Non_administrator_cannot_create_when_administrator_exists()
    {
        _roles.GetRoleAsync(42, Arg.Any<CancellationToken>()).Returns(InvestigationRole.Head);
        _roles.AnyAdministratorAsync(Arg.Any<CancellationToken>()).Returns(true);

        var response = await new CreateDivisionCommand.Handler(_store, _roles, _subject)
            .Handle(new CreateDivisionCommand("СО по г. Бишкек"), CancellationToken.None);

        response.Status.ShouldBeFalse();
        response.StatusMessage.ShouldBe(RoleGuard.AdminDenied);
        await _store.DidNotReceive().CreateAsync(
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<int?>(), Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "Не-Администратор: переименование и выключение тоже отклоняются")]
    public async Task Non_administrator_cannot_rename_or_deactivate()
    {
        _roles.GetRoleAsync(42, Arg.Any<CancellationToken>()).Returns(InvestigationRole.Investigator);
        _roles.AnyAdministratorAsync(Arg.Any<CancellationToken>()).Returns(true);

        var rename = await new RenameDivisionCommand.Handler(_store, _roles, _subject)
            .Handle(new RenameDivisionCommand(3, "Новое"), CancellationToken.None);
        var deactivate = await new SetDivisionActiveCommand.Handler(_store, _roles, _subject)
            .Handle(new SetDivisionActiveCommand(3, false), CancellationToken.None);

        rename.Status.ShouldBeFalse();
        deactivate.Status.ShouldBeFalse();
        await _store.DidNotReceive().RenameAsync(
            Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        await _store.DidNotReceive().SetActiveAsync(Arg.Any<int>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "Администратор создаёт подразделение")]
    public async Task Administrator_may_create()
    {
        _roles.GetRoleAsync(42, Arg.Any<CancellationToken>()).Returns(InvestigationRole.Administrator);
        _roles.AnyAdministratorAsync(Arg.Any<CancellationToken>()).Returns(true);

        var response = await new CreateDivisionCommand.Handler(_store, _roles, _subject)
            .Handle(new CreateDivisionCommand("СО по г. Бишкек", "BSH"), CancellationToken.None);

        response.Status.ShouldBeTrue();
        response.Data.ShouldBe(11);
    }

    [Fact(DisplayName = "Режим первичной настройки (Администратора нет) — создание разрешено любому вошедшему")]
    public async Task Bootstrap_window_allows_creation()
    {
        _roles.GetRoleAsync(42, Arg.Any<CancellationToken>()).Returns((InvestigationRole?)null);
        _roles.AnyAdministratorAsync(Arg.Any<CancellationToken>()).Returns(false);

        var response = await new CreateDivisionCommand.Handler(_store, _roles, _subject)
            .Handle(new CreateDivisionCommand("СО по г. Бишкек"), CancellationToken.None);

        response.Status.ShouldBeTrue();
    }

    [Fact(DisplayName = "Без аутентификации — отказ даже в режиме первичной настройки")]
    public async Task Anonymous_is_denied_even_during_bootstrap()
    {
        _subject.GetCurrentUserIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);
        _roles.AnyAdministratorAsync(Arg.Any<CancellationToken>()).Returns(false);

        var response = await new CreateDivisionCommand.Handler(_store, _roles, _subject)
            .Handle(new CreateDivisionCommand("СО"), CancellationToken.None);

        response.Status.ShouldBeFalse();
    }
}
