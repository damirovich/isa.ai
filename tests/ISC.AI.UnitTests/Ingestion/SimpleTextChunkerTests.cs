using ISC.AI.Ingestion;
using Shouldly;

namespace ISC.AI.UnitTests.Ingestion;

/// <summary>Тесты чанкера по умолчанию (ТО-мат-03).</summary>
public sealed class SimpleTextChunkerTests
{
    [Fact(DisplayName = "Чанкер: пустой/пробельный текст — нет чанков")]
    public void Blank_text_yields_no_chunks()
    {
        new SimpleTextChunker().Chunk("   \n\n  ").ShouldBeEmpty();
    }

    [Fact(DisplayName = "Чанкер: абзацы становятся непустыми фрагментами")]
    public void Paragraphs_become_non_empty_chunks()
    {
        var chunks = new SimpleTextChunker().Chunk("Первый абзац.\n\nВторой абзац.");

        chunks.ShouldNotBeEmpty();
        chunks.ShouldAllBe(c => c.Trim().Length > 0);
    }

    [Fact(DisplayName = "Чанкер: длинный абзац режется по длине")]
    public void Long_paragraph_is_split()
    {
        var chunks = new SimpleTextChunker().Chunk(new string('a', 2500));

        chunks.Count.ShouldBeGreaterThan(1);
    }
}
