using System.Threading;
using System.Threading.Tasks;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.Admin.Application;
using ISC.AI.Modules.Admin.Application.Features.Clearances;
using ISC.AI.Modules.Admin.Domain.Services;
using NSubstitute;
using Shouldly;
using Xunit;

namespace ISC.AI.UnitTests.Admin;

/// <summary>
/// Экран допусков пакета «Администрирование платформы» (ТБ-011/020/021): кому он открыт и как
/// разбирает номера подразделений.
/// </summary>
/// <remarks>
/// Эти случаи проверялись у профиля «ИнспекторAI» до выделения пакета (ADR-0023) и переехали сюда
/// вместе со сценарием. Проверяется ровно то, что в самом сценарии и осталось после выделения: право
/// спрашивается у ПРОФИЛЯ (порт <see cref="IPlatformAdministration"/>), а наименования подразделений —
/// у его справочника (<see cref="IDivisionCatalog"/>).
/// </remarks>
public sealed class ListUserClearancesQueryTests
{
    private readonly IClearanceStore _clearances = Substitute.For<IClearanceStore>();
    private readonly IDivisionCatalog _divisions = Substitute.For<IDivisionCatalog>();
    private readonly IPlatformAdministration _administration = Substitute.For<IPlatformAdministration>();

    [Fact(DisplayName = "Без права администрирования — отказ, и перечень допусков даже не запрашивается (ТБ-012)")]
    public async Task Denies_without_administration_right()
    {
        _administration.CanManageAsync(Arg.Any<CancellationToken>()).Returns(false);

        var response = await ListAsync();

        response.Status.ShouldBeFalse();
        response.StatusMessage.ShouldBe(AdminGuard.Denied);

        // Fail-closed по-настоящему: при отказе хранилище не читается вовсе. Иначе перечень допусков
        // (кто и что вправе видеть) успевал бы покинуть хранилище и осел бы в памяти процесса.
        await _clearances.DidNotReceive().ListAsync(Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "Номер подразделения, которого нет в справочнике, помечается как неизвестный")]
    public async Task Unknown_division_ids_are_reported()
    {
        _administration.CanManageAsync(Arg.Any<CancellationToken>()).Returns(true);
        _divisions.ListAsync(Arg.Any<CancellationToken>())
            .Returns([new DivisionCatalogItem(5, "Территориальная инспекция", IsActive: true)]);
        _clearances.ListAsync(Arg.Any<CancellationToken>())
            .Returns([new ClearanceRow(42, "Иванов И.И.", IsActive: true, MaxClassification: 2, [5, 777])]);

        var row = (await ListAsync()).Data.ShouldNotBeNull().ShouldHaveSingleItem();

        row.HasClearance.ShouldBeTrue();
        row.UnknownDivisionIds.ShouldBe([777]);
        row.Divisions.Single(d => d.Id == 5).Name.ShouldBe("Территориальная инспекция");
        row.Divisions.Single(d => d.Id == 777).IsKnown.ShouldBeFalse();
    }

    [Fact(DisplayName = "Недействующее подразделение показывается наименованием, а не «неизвестным номером»")]
    public async Task Inactive_division_is_still_named()
    {
        // Закрытое подразделение остаётся в выданных допусках. Если бы справочник отдавал только
        // действующие, экран пометил бы такой допуск как «неизвестный номер» — и администратор
        // принялся бы чинить то, что не сломано.
        _administration.CanManageAsync(Arg.Any<CancellationToken>()).Returns(true);
        _divisions.ListAsync(Arg.Any<CancellationToken>())
            .Returns([new DivisionCatalogItem(9, "Упразднённый отдел", IsActive: false)]);
        _clearances.ListAsync(Arg.Any<CancellationToken>())
            .Returns([new ClearanceRow(7, "Петров П.П.", IsActive: true, MaxClassification: 1, [9])]);

        var row = (await ListAsync()).Data.ShouldNotBeNull().ShouldHaveSingleItem();

        row.UnknownDivisionIds.ShouldBeEmpty();
        row.Divisions.ShouldHaveSingleItem().Name.ShouldBe("Упразднённый отдел");
    }

    private async Task<ISC.AI.Abstractions.Application.ResponseDto<IReadOnlyList<UserClearanceRow>>> ListAsync()
    {
        var handler = new ListUserClearancesQuery.Handler(_clearances, _divisions, _administration);
        return await handler.Handle(new ListUserClearancesQuery(), CancellationToken.None);
    }
}
