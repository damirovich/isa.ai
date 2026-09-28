using FluentValidation;
using ISC.AI.Profile.Investigation.Domain.Services;

namespace ISC.AI.Profile.Investigation.Application.Features.Persons;

/// <summary>
/// Правила анкеты объекта (ТФ-ПЕР-05): все поля необязательны; дата и год рождения правдоподобны и не
/// противоречат друг другу. Пределы длин — единственный источник и для таблицы (<c>PersonConfiguration</c>),
/// и для <c>MaxLength</c> полей формы анкеты.
/// </summary>
public sealed class PersonQuestionnaireValidator : AbstractValidator<PersonQuestionnaire>
{
    /// <summary>Самый ранний допустимый год рождения.</summary>
    public const int MinBirthYear = 1900;

    /// <summary>Предел длины места рождения и места работы.</summary>
    public const int MaxPlaceLength = 500;

    /// <summary>Предел длины места жительства.</summary>
    public const int MaxResidenceLength = 1000;

    /// <summary>Предел длины псевдонима.</summary>
    public const int MaxAliasLength = 200;

    /// <summary>Год и дата — не раньше 1900 и не в будущем; при обоих заданных год равен году даты; пол из перечня.</summary>
    public PersonQuestionnaireValidator()
    {
        RuleFor(q => q.BirthYear)
            .Must(year => year is null || (year >= MinBirthYear && year <= DateTime.Today.Year))
            .WithMessage($"Год рождения — от {MinBirthYear} до текущего.");
        RuleFor(q => q.BirthDate)
            .Must(date => date is null
                || (date.Value.Year >= MinBirthYear && date.Value <= DateOnly.FromDateTime(DateTime.Today)))
            .WithMessage($"Дата рождения — не раньше {MinBirthYear} года и не в будущем.");
        RuleFor(q => q)
            .Must(q => q.BirthDate is null || q.BirthYear is null || q.BirthYear == q.BirthDate.Value.Year)
            .WithMessage("Год рождения не совпадает с датой рождения.");
        RuleFor(q => q.Sex).IsInEnum().When(q => q.Sex is not null).WithMessage("Неизвестное значение пола.");
        RuleFor(q => q.BirthPlace).MaximumLength(MaxPlaceLength);
        RuleFor(q => q.WorkPlace).MaximumLength(MaxPlaceLength);
        RuleFor(q => q.Residence).MaximumLength(MaxResidenceLength);
        RuleFor(q => q.Alias).MaximumLength(MaxAliasLength);
    }
}
