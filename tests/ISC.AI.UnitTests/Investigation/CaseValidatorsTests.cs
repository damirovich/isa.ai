using ISC.AI.Profile.Investigation.Application.Features.Cases;
using ISC.AI.Profile.Investigation.Domain.Enums;
using Shouldly;

namespace ISC.AI.UnitTests.Investigation;

/// <summary>Правила форм дел и оснований поиска (ТФ-ДЕЛ-01, ТБ-024, ТБ-071) без БД.</summary>
public sealed class CaseValidatorsTests
{
    private readonly CreateCaseValidator _create = new();
    private readonly UpdateCaseValidator _update = new();
    private readonly AddSearchAuthorizationValidator _authorization = new();

    private static CreateCaseCommand Valid() => new(
        "УД-1", "Кража", CaseKind.CriminalCase, new DateOnly(2026, 9, 1), null, DivisionId: 5, Classification: 1);

    [Fact(DisplayName = "Создание: корректная команда проходит")]
    public void Create_accepts_valid_command() => _create.Validate(Valid()).IsValid.ShouldBeTrue();

    [Fact(DisplayName = "Создание: пустой номер отклоняется")]
    public void Create_rejects_empty_number() =>
        _create.Validate(Valid() with { Number = "  " }).IsValid.ShouldBeFalse();

    [Fact(DisplayName = "Создание: номер длиннее 100 символов отклоняется")]
    public void Create_rejects_overlong_number() =>
        _create.Validate(Valid() with { Number = new string('1', 101) }).IsValid.ShouldBeFalse();

    [Fact(DisplayName = "Создание: название длиннее 500 символов отклоняется")]
    public void Create_rejects_overlong_title() =>
        _create.Validate(Valid() with { Title = new string('а', 501) }).IsValid.ShouldBeFalse();

    // Пределы длин — константы валидатора: их же читают MaxLength полей страниц (Cases, CaseCard), поэтому
    // здесь закрепляются и значения, и то, что валидатор им действительно следует.
    [Fact(DisplayName = "Пределы длин: константы валидатора равны 100/500/2000 и применяются к номеру, названию, основанию")]
    public void Length_limits_are_constants_and_enforced()
    {
        CreateCaseValidator.MaxNumberLength.ShouldBe(100);
        CreateCaseValidator.MaxTitleLength.ShouldBe(500);
        CreateCaseValidator.MaxBasisLength.ShouldBe(2000);

        _create.Validate(Valid() with { Number = new string('1', CreateCaseValidator.MaxNumberLength) }).IsValid.ShouldBeTrue();
        _create.Validate(Valid() with { Title = new string('а', CreateCaseValidator.MaxTitleLength) }).IsValid.ShouldBeTrue();
        _create.Validate(Valid() with { Basis = new string('о', CreateCaseValidator.MaxBasisLength) }).IsValid.ShouldBeTrue();
        _create.Validate(Valid() with { Basis = new string('о', CreateCaseValidator.MaxBasisLength + 1) }).IsValid.ShouldBeFalse();
    }

    [Fact(DisplayName = "Правка: пределы названия и основания — те же константы, что при создании")]
    public void Update_uses_the_same_length_limits()
    {
        var valid = new UpdateCaseCommand(3, "Кража", CaseKind.Material, new DateOnly(2026, 9, 1), null);

        _update.Validate(valid with { Title = new string('а', CreateCaseValidator.MaxTitleLength) }).IsValid.ShouldBeTrue();
        _update.Validate(valid with { Title = new string('а', CreateCaseValidator.MaxTitleLength + 1) }).IsValid.ShouldBeFalse();
        _update.Validate(valid with { Basis = new string('о', CreateCaseValidator.MaxBasisLength) }).IsValid.ShouldBeTrue();
        _update.Validate(valid with { Basis = new string('о', CreateCaseValidator.MaxBasisLength + 1) }).IsValid.ShouldBeFalse();
    }

    [Fact(DisplayName = "Создание: неизвестный вид дела отклоняется")]
    public void Create_rejects_unknown_kind() =>
        _create.Validate(Valid() with { Kind = (CaseKind)42 }).IsValid.ShouldBeFalse();

    [Theory(DisplayName = "Создание: гриф вне шкалы 0..9 отклоняется")]
    [InlineData(-1)]
    [InlineData(10)]
    public void Create_rejects_classification_outside_scale(short classification) =>
        _create.Validate(Valid() with { Classification = classification }).IsValid.ShouldBeFalse();

    [Fact(DisplayName = "Создание: подразделение обязательно — без него дело не заводится (ТБ-024)")]
    public void Create_rejects_missing_division() =>
        _create.Validate(Valid() with { DivisionId = 0 }).IsValid.ShouldBeFalse();

    [Fact(DisplayName = "Правка: неположительный идентификатор отклоняется")]
    public void Update_rejects_non_positive_id() =>
        _update.Validate(new UpdateCaseCommand(0, "Кража", CaseKind.Material, new DateOnly(2026, 9, 1), null))
            .IsValid.ShouldBeFalse();

    [Fact(DisplayName = "Правка: корректная команда проходит")]
    public void Update_accepts_valid_command() =>
        _update.Validate(new UpdateCaseCommand(3, "Кража", CaseKind.Material, new DateOnly(2026, 9, 1), 42))
            .IsValid.ShouldBeTrue();

    [Fact(DisplayName = "Основание: пустые реквизиты отклоняются (ТБ-071)")]
    public void Authorization_rejects_empty_reference() =>
        _authorization.Validate(new AddSearchAuthorizationCommand(
            3, AuthorizationKind.Resolution, "", new DateOnly(2026, 9, 1))).IsValid.ShouldBeFalse();

    [Fact(DisplayName = "Основание: срок действия раньше даты выдачи отклоняется")]
    public void Authorization_rejects_valid_until_before_issue() =>
        _authorization.Validate(new AddSearchAuthorizationCommand(
            3, AuthorizationKind.Resolution, "№ 12", new DateOnly(2026, 9, 10), new DateOnly(2026, 9, 1)))
            .IsValid.ShouldBeFalse();

    [Fact(DisplayName = "Основание: корректная команда проходит")]
    public void Authorization_accepts_valid_command() =>
        _authorization.Validate(new AddSearchAuthorizationCommand(
            3, AuthorizationKind.InvestigatorOrder, "Поручение № 7", new DateOnly(2026, 9, 1), new DateOnly(2026, 12, 31)))
            .IsValid.ShouldBeTrue();
}
