using ISC.AI.Ingestion;
using Shouldly;

namespace ISC.AI.UnitTests.Ingestion;

/// <summary>Дата документа из заголовка (пакеты ЦБД даты не несут) — первая датировка после «от».</summary>
public sealed class TitleDateParserTests
{
    [Theory(DisplayName = "Дата из заголовка: русская датировка словами и цифрами, первая после «от»")]
    [InlineData("Кодекс КР от 28 октября 2021 года № 128 \"Кодекс Кыргызской Республики о правонарушениях\"", 2021, 10, 28)]
    [InlineData("Конституционный Закон КР от 2 июля 2011 года № 68 \"О выборах Президента\"", 2011, 7, 2)]
    [InlineData("\"Конституция Кыргызской Республики\" от 5 мая 2021 года (принятый референдумом 11 апреля 2021 года)", 2021, 5, 5)]
    [InlineData("Закон КР от 15.07.2021 № 84", 2021, 7, 15)]
    [InlineData("Закон Кирг. ССР от 14 апреля 1990 года № 62-XII", 1990, 4, 14)]
    public void Parses_first_date_after_ot(string title, int year, int month, int day)
    {
        TitleDateParser.TryParse(title).ShouldBe(new DateOnly(year, month, day));
    }

    [Theory(DisplayName = "Без даты или с невозможной датой — null, не исключение")]
    [InlineData("КОНСТИТУЦИЯ КИРГИЗСКОЙ СОВЕТСКОЙ СОЦИАЛИСТИЧЕСКОЙ РЕСПУБЛИКИ")]
    [InlineData("Закон от 31 февраля 2020 года № 1")]
    [InlineData("")]
    [InlineData(null)]
    public void Returns_null_when_absent_or_invalid(string? title)
    {
        TitleDateParser.TryParse(title).ShouldBeNull();
    }
}
