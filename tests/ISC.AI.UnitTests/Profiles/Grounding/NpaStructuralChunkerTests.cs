using ISC.AI.Profile.Inspector.Application.Features.Ingestion;
using Shouldly;

namespace ISC.AI.UnitTests.Profiles.Grounding;

/// <summary>
/// Структурный чанкер НПА (Э4-18, §5.3.1.3): разрез по статьям; фолбэк на абзацы без структуры.
/// С 2026-08-19: заголовки распознаются и внутри строки (пакеты без переносов), длинный абзац
/// режется по границам предложения/слова, а не по длине посреди слова.
/// </summary>
public sealed class NpaStructuralChunkerTests
{
    private readonly NpaStructuralChunker _chunker = new();

    [Fact(DisplayName = "Чанкер НПА: разрез по «Статья N» — по чанку на статью")]
    public void Splits_by_article()
    {
        var chunks = _chunker.Chunk("Статья 1. Термины.\nОпределения.\n\nСтатья 2. Область.\nСфера действия.");

        chunks.Count.ShouldBe(2);
        chunks[0].ShouldStartWith("Статья 1");
        chunks[1].ShouldStartWith("Статья 2");
    }

    [Fact(DisplayName = "Чанкер НПА: текст без структуры — абзацный фолбэк (≥1 непустой чанк)")]
    public void Falls_back_without_structure()
    {
        var chunks = _chunker.Chunk("Просто внутренний документ без статей.\n\nВторой абзац.");

        chunks.ShouldNotBeEmpty();
        chunks.ShouldAllBe(c => c.Length > 0);
    }

    [Fact(DisplayName = "Чанкер НПА: заголовки статей ВНУТРИ строки (пакет без переносов) — тоже граница")]
    public void Splits_by_inline_article_headers()
    {
        // Так приходил текст из ЦБД до сохранения структуры: одной строкой, «Статья N.» внутри неё.
        var chunks = _chunker.Chunk(
            "ЗАКОН О бюджете. Статья 1. Утвердить бюджет на 1993 год. Статья 2. Определить доходы. Статья 3. Расходы направить.");

        chunks.Count.ShouldBe(4); // преамбула + 3 статьи
        chunks[1].ShouldStartWith("Статья 1.");
        chunks[2].ShouldStartWith("Статья 2.");
        chunks[3].ShouldStartWith("Статья 3.");
    }

    [Fact(DisplayName = "Чанкер НПА: ссылка «в статье 3 настоящего Закона» — НЕ заголовок, границы не даёт")]
    public void Inline_reference_is_not_a_header()
    {
        var chunks = _chunker.Chunk(
            "Статья 4. В случаях, указанных в статье 3 настоящего Закона, Президент предупреждает о введении положения.\n\n"
            + "Статья 5. Жогорку Кенеш вводит положение.");

        chunks.Count.ShouldBe(2);
        chunks[0].ShouldContain("в статье 3 настоящего Закона");
    }

    [Fact(DisplayName = "Чанкер НПА: римские номера глав «Глава II» — граница")]
    public void Splits_by_roman_chapter()
    {
        var chunks = _chunker.Chunk(
            "Глава I\nОбщие положения\n\nТекст главы.\n\nГлава II\nУсловия введения\n\nТекст второй главы.");

        chunks.Count.ShouldBe(2);
        chunks[1].ShouldStartWith("Глава II");
    }

    [Fact(DisplayName = "Чанкер НПА: длинный абзац режется по границе предложения, не посреди слова")]
    public void Long_paragraph_is_split_at_boundaries()
    {
        // Один абзац из повторяющихся предложений — длиннее жёсткого потолка в несколько раз.
        var sentence = "Республиканский бюджет Кыргызской Республики утверждается по доходам и расходам. ";
        var paragraph = string.Concat(Enumerable.Repeat(sentence, 80)).Trim();

        var chunks = _chunker.Chunk(paragraph);

        chunks.Count.ShouldBeGreaterThan(1);
        chunks.ShouldAllBe(c => c.Length <= 1800);
        // Границы — по концу предложения: каждый чанк начинается с заглавной и кончается точкой.
        chunks.ShouldAllBe(c => c.EndsWith('.') && char.IsUpper(c[0]));
        string.Join(' ', chunks).ShouldBe(paragraph);
    }

    [Fact(DisplayName = "Чанкер НПА: пустой текст — пусто")]
    public void Empty_text()
    {
        _chunker.Chunk("   ").ShouldBeEmpty();
    }
}
