using System.Globalization;
using ISC.AI.Profile.Investigation.Domain.Enums;
using ISC.AI.Profile.Investigation.Domain.Services;

namespace ISC.AI.Profile.Investigation.UI;

/// <summary>
/// Изменяемая модель анкеты объекта (ТФ-ПЕР-05) для форм фигуранта (карточка дела, карточка фигуранта).
/// Все поля необязательны; при выбранной дате рождения год берётся из неё.
/// </summary>
public sealed class PersonQuestionnaireForm
{
    /// <summary>Дата рождения (тип пикера MudBlazor).</summary>
    public DateTime? BirthDate { get; set; }

    /// <summary>Год рождения (когда известен только год).</summary>
    public int? BirthYear { get; set; }

    /// <summary>Место рождения.</summary>
    public string? BirthPlace { get; set; }

    /// <summary>Место работы.</summary>
    public string? WorkPlace { get; set; }

    /// <summary>Место жительства.</summary>
    public string? Residence { get; set; }

    /// <summary>Пол.</summary>
    public PersonSex? Sex { get; set; }

    /// <summary>Псевдоним (оперативная кличка).</summary>
    public string? Alias { get; set; }

    /// <summary>Модель по анкете фигуранта (пустая — для нового).</summary>
    public static PersonQuestionnaireForm From(PersonQuestionnaire? questionnaire)
    {
        var q = questionnaire ?? PersonQuestionnaire.Empty;
        return new PersonQuestionnaireForm
        {
            BirthDate = q.BirthDate?.ToDateTime(TimeOnly.MinValue),
            BirthYear = q.BirthYear,
            BirthPlace = q.BirthPlace,
            WorkPlace = q.WorkPlace,
            Residence = q.Residence,
            Sex = q.Sex,
            Alias = q.Alias,
        };
    }

    /// <summary>Анкета для команды: пустые строки — <see langword="null"/>, год — из даты, если она есть.</summary>
    public PersonQuestionnaire ToQuestionnaire()
    {
        DateOnly? date = BirthDate is { } d ? DateOnly.FromDateTime(d) : null;
        return new PersonQuestionnaire(
            date,
            date?.Year ?? BirthYear,
            Clean(BirthPlace),
            Clean(WorkPlace),
            Clean(Residence),
            Sex,
            Clean(Alias));
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>Подписи анкеты для карточек.</summary>
public static class QuestionnaireLabels
{
    /// <summary>Дата рождения «дд.мм.гггг», иначе «гггг г.», иначе «—».</summary>
    public static string Birth(PersonQuestionnaire? questionnaire) => questionnaire switch
    {
        { BirthDate: { } date } => date.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture),
        { BirthYear: { } year } => year.ToString(CultureInfo.InvariantCulture) + " г.",
        _ => "—",
    };

    /// <summary>
    /// Дата рождения с возрастом на <paramref name="today"/>: «01.02.1998 · 28 лет». Возраст — только при точной дате:
    /// по одному году он был бы неточен на год, и это не показывается; без даты — как <see cref="Birth"/>.
    /// </summary>
    public static string BirthWithAge(PersonQuestionnaire? questionnaire, DateOnly today)
    {
        if (questionnaire?.BirthDate is not { } date || date > today)
        {
            return Birth(questionnaire);
        }

        var age = today.Year - date.Year - (today < date.AddYears(today.Year - date.Year) ? 1 : 0);
        return Birth(questionnaire) + " · " + age.ToString(CultureInfo.InvariantCulture) + " " + YearsWord(age);
    }

    /// <summary>Значение или «—».</summary>
    public static string OrDash(string? value) => string.IsNullOrWhiteSpace(value) ? "—" : value;

    // Склонение: 1 год, 2–4 года, 5–20 лет, 21 год…
    private static string YearsWord(int years)
    {
        var lastTwo = years % 100;
        var last = years % 10;
        return last == 1 && lastTwo != 11 ? "год"
            : last is >= 2 and <= 4 && lastTwo is < 12 or > 14 ? "года"
            : "лет";
    }
}
