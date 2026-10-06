using System;
using ISC.AI.Profile.Investigation.Domain.Services;
using ISC.AI.Profile.Investigation.UI;
using Shouldly;
using Xunit;

namespace ISC.AI.UnitTests.Investigation.UI;

/// <summary>
/// Дата рождения в анкете фигуранта (ТФ-ПЕР-05): с возрастом только при точной дате, с правильным склонением;
/// по одному году возраст не показывается (был бы неточен), без данных — «—».
/// </summary>
public sealed class QuestionnaireLabelsTests
{
    private static readonly DateOnly Today = new(2026, 10, 6);

    [Theory(DisplayName = "Возраст считается по дню рождения и склоняется: год / года / лет")]
    [InlineData(1998, 2, 1, "01.02.1998 · 28 лет")]
    [InlineData(2005, 10, 6, "06.10.2005 · 21 год")]   // день рождения сегодня
    [InlineData(2005, 10, 7, "07.10.2005 · 20 лет")]   // завтра — ещё 20
    [InlineData(2003, 1, 1, "01.01.2003 · 23 года")]
    [InlineData(2015, 5, 5, "05.05.2015 · 11 лет")]
    [InlineData(2025, 1, 1, "01.01.2025 · 1 год")]
    public void Birth_with_age(int year, int month, int day, string expected)
    {
        QuestionnaireLabels.BirthWithAge(new PersonQuestionnaire(BirthDate: new DateOnly(year, month, day)), Today).ShouldBe(expected);
    }

    [Fact(DisplayName = "Только год рождения — без возраста; дата в будущем и пустая анкета — без возраста")]
    public void No_age_without_exact_date()
    {
        QuestionnaireLabels.BirthWithAge(new PersonQuestionnaire(BirthYear: 1990), Today).ShouldBe("1990 г.");
        QuestionnaireLabels.BirthWithAge(new PersonQuestionnaire(BirthDate: new DateOnly(2030, 1, 1)), Today).ShouldBe("01.01.2030");
        QuestionnaireLabels.BirthWithAge(null, Today).ShouldBe("—");
    }
}
