using ISC.AI.AI.Grounding;
using ISC.AI.Abstractions.Grounding;
using ISC.AI.Abstractions.Retrieval;
using NSubstitute;
using Shouldly;

namespace ISC.AI.UnitTests.Grounding;

/// <summary>
/// Тесты грунтовки (ТБ-040, GATE-2, ADR-0013): ссылка подтверждается только из АКТУАЛЬНОГО фрагмента;
/// отсутствующая — непроверенная (вывод не «готов»); найденная лишь в неактуальном — утратила силу.
/// </summary>
[Trait("Category", "Gate")]
public sealed class GroundingValidatorTests
{
    private static RetrievedChunk Chunk(int id, string text, bool isCurrent) =>
        new(ChunkId: id, DocumentId: 1, Text: text, Classification: 0, DivisionId: 0, IsCurrent: isCurrent, Score: 0);

    private static GroundingValidator Validator(params string[] citations)
    {
        var extractor = Substitute.For<ICitationExtractor>();
        extractor.Extract(Arg.Any<string>()).Returns(citations);
        return new GroundingValidator(extractor, new IdentityCitationNormalizer());
    }

    [Fact(DisplayName = "GATE-2: ссылка из актуального фрагмента — Confirmed")]
    public void Citation_in_current_fragment_is_confirmed()
    {
        var result = Validator("Закон N1").Validate(
            "...согласно Закон N1...",
            [Chunk(10, "текст Закон N1 о порядке", isCurrent: true)]);

        result.AllConfirmed.ShouldBeTrue();
        result.Citations.ShouldHaveSingleItem();
        result.Citations[0].Status.ShouldBe(CitationStatus.Confirmed);
        result.Citations[0].SourceChunkId.ShouldBe(10);
    }

    [Fact(DisplayName = "GATE-2: ссылка вне фрагментов — Unverified, вывод не «готов»")]
    public void Citation_absent_is_unverified()
    {
        var result = Validator("Закон N99").Validate(
            "...ссылается на Закон N99...",
            [Chunk(10, "текст Закон N1", isCurrent: true)]);

        result.AllConfirmed.ShouldBeFalse();
        result.Citations[0].Status.ShouldBe(CitationStatus.Unverified);
    }

    [Fact(DisplayName = "GATE-2: ссылка только в неактуальном фрагменте — Superseded")]
    public void Citation_in_stale_fragment_is_superseded()
    {
        var result = Validator("Закон N5").Validate(
            "...по Закон N5...",
            [Chunk(20, "старый текст Закон N5", isCurrent: false)]);

        result.AllConfirmed.ShouldBeFalse();
        result.Citations[0].Status.ShouldBe(CitationStatus.Superseded);
        result.Citations[0].SourceChunkId.ShouldBe(20);
    }

    [Fact(DisplayName = "Вывод без ссылок — проверять нечего (AllConfirmed)")]
    public void No_citations_is_all_confirmed()
    {
        var result = Validator().Validate("обычный текст без ссылок", []);

        result.AllConfirmed.ShouldBeTrue();
        result.Citations.ShouldBeEmpty();
    }
}
