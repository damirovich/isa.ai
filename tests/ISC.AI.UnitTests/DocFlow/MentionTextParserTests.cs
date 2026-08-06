using ISC.AI.Modules.DocFlow.Domain.Services;
using Shouldly;

namespace ISC.AI.UnitTests.DocFlow;

/// <summary>
/// Разбор упоминаний в тексте комментария (§4.8, этап 2.2 Э4-35). Чистая функция — граничные случаи
/// фиксируются здесь, потому что в СКИД сам парсер тестами покрыт был, а лимиты формы — нет.
/// </summary>
public sealed class MentionTextParserTests
{
    [Theory(DisplayName = "Упоминания: пустой ввод не даёт идентификаторов")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("текст без упоминаний")]
    public void Extract_returns_empty_for_input_without_mentions(string? content) =>
        MentionTextParser.ExtractMentionedUserIds(content).ShouldBeEmpty();

    [Fact(DisplayName = "Упоминания: несколько токенов извлекаются по порядку, повтор схлопывается")]
    public void Extract_returns_distinct_ids_in_order()
    {
        var ids = MentionTextParser.ExtractMentionedUserIds(
            "Прошу @[Иванов И.И.](user:10) и @[Петров П.П.](user:20) проверить, @[Иванов И.И.](user:10) особенно");

        ids.ShouldBe([10, 20]);
    }

    [Fact(DisplayName = "Упоминания: нечисловой идентификатор игнорируется, валидные рядом сохраняются")]
    public void Extract_skips_malformed_identifiers()
    {
        var ids = MentionTextParser.ExtractMentionedUserIds(
            "@[Кто-то](user:не-число) и @[Сидоров](user:7)");

        ids.ShouldBe([7]);
    }

    [Fact(DisplayName = "Упоминания: токен без ведущего @ не считается упоминанием")]
    public void Extract_requires_leading_at_sign()
    {
        MentionTextParser.ExtractMentionedUserIds("[Иванов](user:10)").ShouldBeEmpty();
    }
}
