using FluentValidation;
using ISC.AI.Profile.Investigation.Domain.Services;

namespace ISC.AI.Profile.Investigation.Application.Features.Cases;

/// <summary>
/// Правила реквизитов задания по объекту (ТФ-ДЕЛ-05): обязательны № задания, подразделение-инициатор,
/// обоснование и цель (умолчание по вопросу 19 Приложения В ТЗ). Пределы длин — единственный источник и для
/// таблицы (<c>CaseFileConfiguration</c>), и для <c>MaxLength</c> полей формы задания.
/// </summary>
/// <remarks>
/// Существование и вид записей справочника (ГУ, звание, должность) проверяет хранилище: валидатор БД не
/// видит, а без этой проверки задание могло бы сослаться на «звание» вместо «ГУ».
/// </remarks>
public sealed class TaskRequisitesValidator : AbstractValidator<TaskRequisites>
{
    /// <summary>Предел длины № задания.</summary>
    public const int MaxTaskNumberLength = 100;

    /// <summary>Предел длины ФИО инициатора.</summary>
    public const int MaxInitiatorNameLength = 300;

    /// <summary>Предел длины телефона инициатора.</summary>
    public const int MaxInitiatorPhoneLength = 50;

    /// <summary>Предел длины прочих служебных реквизитов инициатора.</summary>
    public const int MaxInitiatorDetailsLength = 1000;

    /// <summary>Предел длины обоснования мероприятия.</summary>
    public const int MaxJustificationLength = 4000;

    /// <summary>Предел длины цели мероприятия.</summary>
    public const int MaxPurposeLength = 2000;

    /// <summary>Предел длины примечания к заданию.</summary>
    public const int MaxNotesLength = 4000;

    /// <summary>Текст отказа: у задания нет реквизитов.</summary>
    public const string TaskRequired = "Для задания по объекту заполните реквизиты задания (ТФ-ДЕЛ-05).";

    /// <summary>Текст отказа: реквизиты задания у дела другого вида.</summary>
    public const string TaskOnlyForTaskKind = "Реквизиты задания указываются только у дела вида «Задание по объекту».";

    /// <summary>Обязательные поля непусты, ссылки на справочник положительны, длины в пределах.</summary>
    public TaskRequisitesValidator()
    {
        RuleFor(t => t.TaskNumber).NotEmpty().WithMessage("Укажите № задания.").MaximumLength(MaxTaskNumberLength);
        RuleFor(t => t.InitiatorUnitId).GreaterThan(0).WithMessage("Выберите подразделение-инициатор (ГУ).");
        RuleFor(t => t.Justification).NotEmpty().WithMessage("Укажите обоснование мероприятия.").MaximumLength(MaxJustificationLength);
        RuleFor(t => t.Purpose).NotEmpty().WithMessage("Укажите цель мероприятия.").MaximumLength(MaxPurposeLength);
        RuleFor(t => t.InitiatorName).MaximumLength(MaxInitiatorNameLength);
        RuleFor(t => t.InitiatorRankId).GreaterThan(0).When(t => t.InitiatorRankId is not null);
        RuleFor(t => t.InitiatorPositionId).GreaterThan(0).When(t => t.InitiatorPositionId is not null);
        RuleFor(t => t.InitiatorPhone).MaximumLength(MaxInitiatorPhoneLength);
        RuleFor(t => t.InitiatorDetails).MaximumLength(MaxInitiatorDetailsLength);
        RuleFor(t => t.Notes).MaximumLength(MaxNotesLength);
    }
}
