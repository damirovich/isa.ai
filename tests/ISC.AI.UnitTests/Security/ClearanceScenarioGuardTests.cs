using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Inspector.Application.Clearances;
using ISC.AI.Profile.Inspector.Domain.Enums;
using ISC.AI.Profile.Inspector.Domain.Services;
using NSubstitute;
using Shouldly;

namespace ISC.AI.UnitTests.Security;

/// <summary>
/// Кто вправе распоряжаться допусками (ТБ-011, §2.1 ТЗ СКИД) и как экран разбирает расхождение со
/// справочником подразделений.
/// </summary>
/// <remarks>
/// Главное, что здесь зафиксировано: охрана опирается на «кто вошёл» (<see cref="ISubjectProvider"/>),
/// а НЕ на контекст допуска. Иначе на ЧИСТОМ контуре, где записи допуска нет ни у кого, экран выдачи
/// допусков был бы недоступен всем — выдать первый допуск некому (замок без ключа, тот же класс
/// отказа, что уже случался с ролями, Э4-35 §6.4.1).
/// </remarks>
public sealed class ClearanceScenarioGuardTests
{
    private readonly IClearanceStore _clearances = Substitute.For<IClearanceStore>();
    private readonly IDivisionAdminStore _divisions = Substitute.For<IDivisionAdminStore>();
    private readonly IUserRoleStore _roles = Substitute.For<IUserRoleStore>();
    private readonly ISubjectProvider _subject = Substitute.For<ISubjectProvider>();

    public ClearanceScenarioGuardTests()
    {
        _subject.GetCurrentUserIdAsync(Arg.Any<CancellationToken>()).Returns((int?)42);
        _clearances.ListAsync(Arg.Any<CancellationToken>()).Returns([]);
        _divisions.ListAsync(Arg.Any<CancellationToken>()).Returns([]);
    }

    [Fact(DisplayName = "Администратор распоряжается допусками")]
    public async Task Administrator_may_manage()
    {
        _roles.GetRoleAsync(42, Arg.Any<CancellationToken>()).Returns(UserRole.Administrator);
        _roles.AnyAdministratorAsync(Arg.Any<CancellationToken>()).Returns(true);

        (await ListAsync()).Status.ShouldBeTrue();
    }

    [Fact(DisplayName = "Не-Администратор при наличии Администратора получает отказ")]
    public async Task Non_administrator_is_denied_when_administrator_exists()
    {
        _roles.GetRoleAsync(42, Arg.Any<CancellationToken>()).Returns(UserRole.Inspector);
        _roles.AnyAdministratorAsync(Arg.Any<CancellationToken>()).Returns(true);

        var response = await ListAsync();
        response.Status.ShouldBeFalse();
        response.StatusMessage.ShouldBe(ClearanceGuard.Denied);
    }

    [Fact(DisplayName = "Пока Администратора нет — открыто любому вошедшему (иначе допуск выдать некому)")]
    public async Task Bootstrap_window_is_open_while_no_administrator_exists()
    {
        _roles.GetRoleAsync(42, Arg.Any<CancellationToken>()).Returns((UserRole?)null);
        _roles.AnyAdministratorAsync(Arg.Any<CancellationToken>()).Returns(false);

        (await ListAsync()).Status.ShouldBeTrue();
    }

    [Fact(DisplayName = "Без аутентификации — отказ, даже в режиме первичной настройки")]
    public async Task Anonymous_is_denied_even_during_bootstrap()
    {
        _subject.GetCurrentUserIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);
        _roles.AnyAdministratorAsync(Arg.Any<CancellationToken>()).Returns(false);

        (await ListAsync()).Status.ShouldBeFalse();
    }

    [Fact(DisplayName = "Экран НЕ требует допуска у распорядителя: контекст допуска здесь не спрашивается")]
    public async Task Guard_never_touches_the_access_context()
    {
        // Провайдер контекста допуска даже не передаётся сценарию — ни один путь не может уронить
        // экран на AccessContextRequiredException, как это делала страница ролей на чистом контуре.
        _roles.GetRoleAsync(42, Arg.Any<CancellationToken>()).Returns(UserRole.Administrator);

        (await ListAsync()).Status.ShouldBeTrue();
        await _subject.Received().GetCurrentUserIdAsync(Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "Номер подразделения, которого нет в справочнике, помечается как неизвестный")]
    public async Task Unknown_division_ids_are_reported()
    {
        _roles.GetRoleAsync(42, Arg.Any<CancellationToken>()).Returns(UserRole.Administrator);
        _divisions.ListAsync(Arg.Any<CancellationToken>())
            .Returns([new DivisionNode(5, "Территориальная инспекция", null, null)]);
        _clearances.ListAsync(Arg.Any<CancellationToken>())
            .Returns([new ClearanceRow(42, "Иванов И.И.", IsActive: true, MaxClassification: 2, [5, 777])]);

        var row = (await ListAsync()).Data.ShouldNotBeNull().ShouldHaveSingleItem();

        row.HasClearance.ShouldBeTrue();
        row.UnknownDivisionIds.ShouldBe([777]);
        row.Divisions.Single(d => d.Id == 5).Name.ShouldBe("Территориальная инспекция");
        row.Divisions.Single(d => d.Id == 777).IsKnown.ShouldBeFalse();
    }

    private async Task<ISC.AI.Abstractions.Application.ResponseDto<IReadOnlyList<UserClearanceRow>>> ListAsync()
    {
        var handler = new ListUserClearancesQuery.Handler(_clearances, _divisions, _roles, _subject);
        return await handler.Handle(new ListUserClearancesQuery(), CancellationToken.None);
    }
}
