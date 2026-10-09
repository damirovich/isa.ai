using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Investigation.Application.Features.Cases;
using ISC.AI.Profile.Investigation.Application.Features.Divisions;
using ISC.AI.Profile.Investigation.Application.Features.Scope;
using ISC.AI.Profile.Investigation.Domain.Enums;
using ISC.AI.Profile.Investigation.Domain.Services;
using ISC.AI.Profile.Investigation.UI;
using NSubstitute;
using Shouldly;
using Xunit;

namespace ISC.AI.UnitTests.Investigation;

/// <summary>
/// Отдел ОН/ОУ (ТЭ-008, ADR-0039): отметка ставится на подразделении, вложенные наследуют её от ближайшего
/// отмеченного вышестоящего; дело относится к отделу через своё подразделение. Отметка проверяется формой и пишется в
/// журнал; отдел в адресе списка разбирается безопасно; плитки стартовой страницы — по подразделениям допуска.
/// </summary>
public sealed class CaseDirectionTests
{
    private static readonly CaseDirection On = CaseDirection.Surveillance;
    private static readonly CaseDirection Ou = CaseDirection.Establishment;

    [Fact(DisplayName = "Отдел подразделения: своя отметка, иначе — ближайшего отмеченного вышестоящего; без отметок — не определён")]
    public void Direction_is_inherited_from_nearest_marked_ancestor()
    {
        // ОПУ (без отметки) → Отдел ОН (ОН) → Группа 1 (—) → Подгруппа (ОУ, своя отметка сильнее) ; Прочее (—)
        var resolved = DivisionDirections.Resolve(
        [
            (1, null, null),
            (2, 1, On),
            (3, 2, null),
            (4, 3, Ou),
            (5, 1, null),
        ]);

        resolved.ContainsKey(1).ShouldBeFalse();
        resolved[2].ShouldBe(On);
        resolved[3].ShouldBe(On);
        resolved[4].ShouldBe(Ou);
        resolved.ContainsKey(5).ShouldBeFalse();
    }

    [Fact(DisplayName = "Цикл в справочнике («А в Б, Б в А») не зацикливает разбор; родитель вне справочника — конец цепочки")]
    public void Cycles_and_missing_parents_are_safe()
    {
        var resolved = DivisionDirections.Resolve(
        [
            (1, 2, null),
            (2, 1, null),
            (3, 4, Ou),
            (4, 3, null),
            (5, 999, null),
        ]);

        resolved.ContainsKey(1).ShouldBeFalse();
        resolved.ContainsKey(2).ShouldBeFalse();
        resolved[3].ShouldBe(Ou);
        resolved[4].ShouldBe(Ou);
        resolved.ContainsKey(5).ShouldBeFalse();
    }

    [Fact(DisplayName = "Форма подразделения: отдел — ОН, ОУ или пусто; иное значение отклоняется с текстом «ОН или ОУ»")]
    public void Division_direction_is_validated()
    {
        new CreateDivisionValidator().Validate(new CreateDivisionCommand("Отдел", Direction: null)).IsValid.ShouldBeTrue();
        new CreateDivisionValidator().Validate(new CreateDivisionCommand("Отдел", Direction: Ou)).IsValid.ShouldBeTrue();
        new RenameDivisionValidator().Validate(new RenameDivisionCommand(3, "Отдел", Direction: On)).IsValid.ShouldBeTrue();

        var create = new CreateDivisionValidator().Validate(new CreateDivisionCommand("Отдел", Direction: (CaseDirection)7));
        create.IsValid.ShouldBeFalse();
        create.Errors.ShouldContain(e => e.ErrorMessage == CreateDivisionValidator.DirectionInvalid);
        new RenameDivisionValidator().Validate(new RenameDivisionCommand(3, "Отдел", Direction: (CaseDirection)3)).IsValid.ShouldBeFalse();
    }

    [Fact(DisplayName = "Журнал: отметка отдела при создании и правке подразделения и отбор списка дел по отделу записываются")]
    public void Direction_goes_to_audit_summary()
    {
        new CreateDivisionCommand("Отдел", Direction: On).AuditSummary.ShouldNotBeNull().ShouldEndWith("direction=Surveillance");
        new CreateDivisionCommand("Отдел").AuditSummary.ShouldNotBeNull().ShouldEndWith("direction=-");
        new RenameDivisionCommand(3, "Отдел", Direction: Ou).AuditSummary.ShouldBe("investigation:division:3:rename:direction=Establishment");
        new ListCasesQuery(Direction: On).AuditSummary.ShouldNotBeNull().ShouldEndWith("direction=Surveillance");
        new ListCasesQuery().AuditSummary.ShouldNotBeNull().ShouldEndWith("direction=-");
    }

    [Theory(DisplayName = "Отдел в адресе списка: «on» — ОН, «ou» — ОУ; пусто и прочее — все отделы")]
    [InlineData("on", CaseDirection.Surveillance)]
    [InlineData("ou", CaseDirection.Establishment)]
    [InlineData(" OU ", CaseDirection.Establishment)]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("1", null)]
    [InlineData("отм", null)]
    public void Direction_query_is_parsed_safely(string? value, CaseDirection? expected) =>
        DirectionQuery.Parse(value).ShouldBe(expected);

    [Fact(DisplayName = "Адрес плитки: /cases?direction=on и /cases?direction=ou — и разбирается обратно")]
    public void Direction_query_round_trips()
    {
        DirectionQuery.CasesUri(On).ShouldBe("/cases?direction=on");
        DirectionQuery.CasesUri(Ou).ShouldBe("/cases?direction=ou");
        foreach (var direction in Enum.GetValues<CaseDirection>())
        {
            DirectionQuery.Parse(DirectionQuery.Code(direction)).ShouldBe(direction);
        }
    }

    [Fact(DisplayName = "Подписи отделов: «ОН — Оперативное наблюдение», «ОУ — Оперативная установка»")]
    public void Direction_labels()
    {
        Enum.GetValues<CaseDirection>().Select(d => d.ShortLabel() + " — " + d.Label())
            .ShouldBe(["ОН — Оперативное наблюдение", "ОУ — Оперативная установка"]);
    }

    [Fact(DisplayName = "Отделы допуска: группа в допуске получает отдел от вышестоящего, даже если само вышестоящее вне допуска")]
    public async Task Access_scope_lists_departments_of_allowed_divisions()
    {
        var access = Substitute.For<IAccessContextProvider>();
        access.GetCurrentAsync(Arg.Any<CancellationToken>()).Returns(new AccessContext("7", 3, [3, 5]));
        var divisions = Substitute.For<IDivisionAdminStore>();
        divisions.ListAsync(Arg.Any<CancellationToken>()).Returns(
        [
            new DivisionNode(1, "ОПУ", null, null, true, 0),
            new DivisionNode(2, "Отдел ОН", null, 1, true, 0, On),
            new DivisionNode(3, "Группа ОН-1", null, 2, true, 1),
            new DivisionNode(4, "Отдел ОУ", null, 1, true, 0, Ou),
            new DivisionNode(5, "Прочее", null, 1, true, 1),
        ]);
        var subject = Substitute.For<ISubjectProvider>();
        subject.GetCurrentUserIdAsync(Arg.Any<CancellationToken>()).Returns(7);

        var response = await new GetAccessScopeQuery.Handler(access, divisions, Substitute.For<IUserRoleStore>(), subject)
            .Handle(new GetAccessScopeQuery(), CancellationToken.None);

        var scope = response.Data.ShouldNotBeNull();
        scope.OwnerDivisions.Select(d => d.Id).ShouldBe([3, 5], ignoreOrder: true);
        scope.Directions.ShouldBe([On]); // ОУ в допуске нет — плитка ОУ будет неактивна
    }
}
