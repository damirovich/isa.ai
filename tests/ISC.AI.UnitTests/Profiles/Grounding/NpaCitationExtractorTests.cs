using ISC.AI.Profile.Inspector.Application.Grounding;
using Shouldly;

namespace ISC.AI.UnitTests.Profiles.Grounding;

/// <summary>Профильный экстрактор ссылок НПА (Э4-18, §5.3.1.2): извлекает надёжно грунтуемые формы.</summary>
public sealed class NpaCitationExtractorTests
{
    private readonly NpaCitationExtractor _extractor = new();

    [Fact(DisplayName = "Экстрактор НПА: статьи/пункты/части/номера/кодексы/Конституция")]
    public void Extracts_npa_reference_forms()
    {
        var citations = _extractor.Extract(
            "Согласно статье 12 и пункту 3, а также ч. 2 Закона № 45 и Уголовного кодекса и Конституции.");

        citations.ShouldContain(c => c.Contains("12"));
        citations.ShouldContain(c => c.Contains('3'));
        citations.ShouldContain(c => c.Contains('2'));
        citations.ShouldContain(c => c.Contains("45"));
        citations.ShouldContain(c => c.Contains("кодекс", StringComparison.OrdinalIgnoreCase));
        citations.ShouldContain(c => c.Contains("онституци", StringComparison.OrdinalIgnoreCase));
    }

    [Fact(DisplayName = "Экстрактор НПА: дедуп повторов (регистронезависимо)")]
    public void Deduplicates()
    {
        var citations = _extractor.Extract("статья 5, ещё раз статья 5, и снова СТАТЬЯ 5");
        citations.Count(c => c.Contains('5')).ShouldBe(1);
    }

    [Fact(DisplayName = "Экстрактор НПА: текст без ссылок — пусто")]
    public void No_citations_in_plain_text()
    {
        _extractor.Extract("Обычный внутренний текст без ссылок на нормы.").ShouldBeEmpty();
    }
}
