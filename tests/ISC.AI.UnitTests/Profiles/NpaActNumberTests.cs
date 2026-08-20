using ISC.AI.Profile.Inspector.Domain.Services;
using Shouldly;

namespace ISC.AI.UnitTests.Profiles;

/// <summary>Номер акта из заголовка НПА — колонка «№» каталога (отдельным полем корпус номер не хранит).</summary>
public sealed class NpaActNumberTests
{
    [Theory(DisplayName = "Номер акта: первый «№ …» заголовка, включая суффиксы «62-XII»")]
    [InlineData("Конституционный Закон КР от 28 июля 2026 года № 135 \"О внесении изменений…\"", "135")]
    [InlineData("Кодекс КР от 28 октября 2021 года № 128 \"Кодекс о правонарушениях\"", "128")]
    [InlineData("Закон Кирг. ССР от 14 апреля 1990 года № 62-XII \"Об утверждении Указов\"", "62-XII")]
    [InlineData("ЗАКОН КЫРГЫЗСКОЙ РЕСПУБЛИКИ от 7 мая 1993 года № 1215-XII О внесении изменений", "1215-XII")]
    public void Parses_first_number(string title, string expected)
    {
        NpaActNumber.TryParse(title).ShouldBe(expected);
    }

    [Theory(DisplayName = "Без номера — null (Конституция, пустой заголовок)")]
    [InlineData("КОНСТИТУЦИЯ КИРГИЗСКОЙ СОВЕТСКОЙ СОЦИАЛИСТИЧЕСКОЙ РЕСПУБЛИКИ")]
    [InlineData("")]
    [InlineData(null)]
    public void Returns_null_when_absent(string? title)
    {
        NpaActNumber.TryParse(title).ShouldBeNull();
    }
}
