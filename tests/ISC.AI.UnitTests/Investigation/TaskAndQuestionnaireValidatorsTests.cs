using ISC.AI.Profile.Investigation.Application.Features.Cases;
using ISC.AI.Profile.Investigation.Application.Features.Persons;
using ISC.AI.Profile.Investigation.Domain.Enums;
using ISC.AI.Profile.Investigation.Domain.Services;
using Shouldly;

namespace ISC.AI.UnitTests.Investigation;

/// <summary>
/// Правила реквизитов задания (ТФ-ДЕЛ-05) и анкеты объекта (ТФ-ПЕР-05) без БД: реквизиты есть ровно у задания,
/// обязательные поля — по умолчанию вопроса 19 Приложения В ТЗ; анкета необязательна, но правдоподобна.
/// </summary>
public sealed class TaskAndQuestionnaireValidatorsTests
{
    private static readonly TaskRequisites Complete = new("З-17/26", 3, "Обоснование", "Цель");

    private readonly CreateCaseValidator _create = new();
    private readonly UpdateCaseValidator _update = new();
    private readonly CreatePersonValidator _person = new();
    private readonly PersonQuestionnaireValidator _questionnaire = new();

    private static CreateCaseCommand TaskCommand(TaskRequisites? task) => new(
        "Т-1", "Задание", CaseKind.ObjectTask, new DateOnly(2026, 9, 1), null, DivisionId: 5, Classification: 1,
        TaskRequisites: task);

    [Fact(DisplayName = "Задание с полными реквизитами проходит и при заведении, и при правке")]
    public void Complete_task_is_valid()
    {
        _create.Validate(TaskCommand(Complete)).IsValid.ShouldBeTrue();
        _update.Validate(new UpdateCaseCommand(7, "Задание", CaseKind.ObjectTask, new DateOnly(2026, 9, 1), null, null, Complete))
            .IsValid.ShouldBeTrue();
    }

    [Fact(DisplayName = "Задание без реквизитов отклоняется с текстом про ТФ-ДЕЛ-05")]
    public void Task_without_requisites_is_rejected()
    {
        var result = _create.Validate(TaskCommand(null));

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.ErrorMessage == TaskRequisitesValidator.TaskRequired);
        _update.Validate(new UpdateCaseCommand(7, "Задание", CaseKind.ObjectTask, new DateOnly(2026, 9, 1), null))
            .IsValid.ShouldBeFalse();
    }

    [Theory(DisplayName = "Реквизиты задания у дела другого вида отклоняются")]
    [InlineData(CaseKind.CriminalCase)]
    [InlineData(CaseKind.Material)]
    [InlineData(CaseKind.OperativeMeasure)]
    public void Requisites_on_other_kinds_are_rejected(CaseKind kind)
    {
        var result = _create.Validate(TaskCommand(Complete) with { Kind = kind });

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.ErrorMessage == TaskRequisitesValidator.TaskOnlyForTaskKind);
    }

    [Fact(DisplayName = "Обязательные реквизиты: пустой № задания, нулевой ГУ, пустые обоснование или цель — отказ")]
    public void Each_required_field_is_enforced()
    {
        _create.Validate(TaskCommand(Complete with { TaskNumber = " " })).IsValid.ShouldBeFalse();
        _create.Validate(TaskCommand(Complete with { InitiatorUnitId = 0 })).IsValid.ShouldBeFalse();
        _create.Validate(TaskCommand(Complete with { Justification = "" })).IsValid.ShouldBeFalse();
        _create.Validate(TaskCommand(Complete with { Purpose = "  " })).IsValid.ShouldBeFalse();
        _create.Validate(TaskCommand(Complete with { InitiatorRankId = 0 })).IsValid.ShouldBeFalse();
    }

    [Fact(DisplayName = "Пределы длин реквизитов задания — константы валидатора и применяются")]
    public void Task_length_limits_are_enforced()
    {
        _create.Validate(TaskCommand(Complete with { TaskNumber = new string('1', TaskRequisitesValidator.MaxTaskNumberLength) }))
            .IsValid.ShouldBeTrue();
        _create.Validate(TaskCommand(Complete with { TaskNumber = new string('1', TaskRequisitesValidator.MaxTaskNumberLength + 1) }))
            .IsValid.ShouldBeFalse();
        _create.Validate(TaskCommand(Complete with { Purpose = new string('ц', TaskRequisitesValidator.MaxPurposeLength + 1) }))
            .IsValid.ShouldBeFalse();
        _create.Validate(TaskCommand(Complete with { InitiatorPhone = new string('0', TaskRequisitesValidator.MaxInitiatorPhoneLength + 1) }))
            .IsValid.ShouldBeFalse();
    }

    [Fact(DisplayName = "Анкета: пустая допустима; полная правдоподобная проходит")]
    public void Questionnaire_empty_and_full_are_valid()
    {
        _questionnaire.Validate(PersonQuestionnaire.Empty).IsValid.ShouldBeTrue();
        _questionnaire.Validate(new PersonQuestionnaire(
            new DateOnly(1985, 3, 12), 1985, "г. Ош", "ОсОО", "г. Бишкек", PersonSex.Female, "Шторм")).IsValid.ShouldBeTrue();
    }

    [Fact(DisplayName = "Анкета: год раньше 1900 или в будущем, дата в будущем, год не совпадает с датой, неизвестный пол — отказ")]
    public void Questionnaire_rejects_implausible_values()
    {
        _questionnaire.Validate(new PersonQuestionnaire(BirthYear: 1899)).IsValid.ShouldBeFalse();
        _questionnaire.Validate(new PersonQuestionnaire(BirthYear: DateTime.Today.Year + 1)).IsValid.ShouldBeFalse();
        _questionnaire.Validate(new PersonQuestionnaire(DateOnly.FromDateTime(DateTime.Today).AddDays(1))).IsValid.ShouldBeFalse();
        _questionnaire.Validate(new PersonQuestionnaire(new DateOnly(1985, 3, 12), 1986)).IsValid.ShouldBeFalse();
        _questionnaire.Validate(new PersonQuestionnaire(Sex: (PersonSex)9)).IsValid.ShouldBeFalse();
        _questionnaire.Validate(new PersonQuestionnaire(Alias: new string('а', PersonQuestionnaireValidator.MaxAliasLength + 1)))
            .IsValid.ShouldBeFalse();
    }

    [Fact(DisplayName = "Фигурант: неизвестная роль и неправдоподобная анкета отклоняются валидатором команды")]
    public void Person_command_checks_role_and_questionnaire()
    {
        _person.Validate(new CreatePersonCommand(3, "Иванов", false, Role: PersonRole.Target)).IsValid.ShouldBeTrue();
        _person.Validate(new CreatePersonCommand(3, "Иванов", false, Role: (PersonRole)42)).IsValid.ShouldBeFalse();
        _person.Validate(new CreatePersonCommand(3, "Иванов", false, Questionnaire: new PersonQuestionnaire(BirthYear: 1800)))
            .IsValid.ShouldBeFalse();
    }
}
