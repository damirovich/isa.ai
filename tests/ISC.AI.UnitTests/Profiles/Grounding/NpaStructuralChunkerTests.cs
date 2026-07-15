using ISC.AI.Profile.Inspector.Application.Ingestion;
using Shouldly;

namespace ISC.AI.UnitTests.Profiles.Grounding;

/// <summary>Структурный чанкер НПА (Э4-18, §5.3.1.3): разрез по статьям; фолбэк на абзацы без структуры.</summary>
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

    [Fact(DisplayName = "Чанкер НПА: пустой текст — пусто")]
    public void Empty_text()
    {
        _chunker.Chunk("   ").ShouldBeEmpty();
    }
}
