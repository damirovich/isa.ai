using ISC.AI.Abstractions.Ingestion;
using ISC.AI.Ingestion;
using ISC.AI.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using NSubstitute;
using Shouldly;

namespace ISC.AI.UnitTests.Ingestion;

/// <summary>
/// Тесты fail-closed загрузки (ТБ-024): документ без явного грифа/подразделения отклоняется и БД не
/// затрагивается. Отсутствие исключения от мока БД подтверждает, что путь записи не достигается.
/// </summary>
public sealed class IngestionFailClosedTests
{
    private static IngestionPort CreatePort() => new(
        Substitute.For<IDbContextFactory<CoreDbContext>>(),
        Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>(),
        new SimpleTextChunker());

    [Fact(DisplayName = "Ingestion fail-closed: без грифа — отказ, без записи (ТБ-024)")]
    public async Task Ingest_without_classification_is_rejected()
    {
        var result = await CreatePort().IngestAsync(
            new IngestionRequest("приказ", "Без грифа", "текст", Classification: null, DivisionId: 7));

        result.Accepted.ShouldBeFalse();
        result.DocumentId.ShouldBeNull();
        result.RejectionReason.ShouldNotBeNullOrEmpty();
    }

    [Fact(DisplayName = "Ingestion fail-closed: без подразделения — отказ (ТБ-024)")]
    public async Task Ingest_without_division_is_rejected()
    {
        var result = await CreatePort().IngestAsync(
            new IngestionRequest("приказ", "Без подразделения", "текст", Classification: 1, DivisionId: null));

        result.Accepted.ShouldBeFalse();
    }
}

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
