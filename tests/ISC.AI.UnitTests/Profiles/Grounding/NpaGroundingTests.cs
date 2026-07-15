using ISC.AI.AI.Grounding;
using ISC.AI.Abstractions.Grounding;
using ISC.AI.Abstractions.Retrieval;
using ISC.AI.Profile.Inspector.Application.Grounding;
using Shouldly;

namespace ISC.AI.UnitTests.Profiles.Grounding;

/// <summary>
/// Сквозная грунтовка НПА (Э4-18, GATE-2): ядровой <see cref="GroundingValidator"/> с ПРОФИЛЬНЫМИ
/// извлечением/нормализацией НПА. Доказывает, что грунтовка реально работает (не заглушка): ссылка из
/// актуального фрагмента — Confirmed; вне фрагментов — Unverified; только из неактуального — Superseded.
/// </summary>
[Trait("Category", "Gate")]
public sealed class NpaGroundingTests
{
    private static GroundingValidator NpaValidator() =>
        new(new NpaCitationExtractor(), new NpaCitationNormalizer());

    private static RetrievedChunk Chunk(int id, string text, bool isCurrent) =>
        new(ChunkId: id, DocumentId: 1, Text: text, Classification: 0, DivisionId: 0, IsCurrent: isCurrent, Score: 0);

    [Fact(DisplayName = "GATE-2 НПА: «статьёй 12» из актуального фрагмента — Confirmed")]
    public void Article_in_current_fragment_confirmed()
    {
        var result = NpaValidator().Validate(
            "В соответствии со статьёй 12 порядок проверки установлен.",
            [Chunk(10, "Статья 12. Порядок проведения проверки.", isCurrent: true)]);

        result.AllConfirmed.ShouldBeTrue();
        result.Citations.ShouldContain(c => c.Status == CitationStatus.Confirmed && c.SourceChunkId == 10);
    }

    [Fact(DisplayName = "GATE-2 НПА: ссылка вне фрагментов — Unverified, вывод не «готов»")]
    public void Article_absent_unverified()
    {
        var result = NpaValidator().Validate(
            "Согласно статье 99 требуется дополнительное согласование.",
            [Chunk(10, "Статья 12. Порядок проведения проверки.", isCurrent: true)]);

        result.AllConfirmed.ShouldBeFalse();
        result.Citations.ShouldContain(c => c.Status == CitationStatus.Unverified);
    }

    [Fact(DisplayName = "GATE-2 НПА: ссылка только в неактуальном фрагменте — Superseded")]
    public void Article_only_in_stale_superseded()
    {
        var result = NpaValidator().Validate(
            "Согласно статье 12 порядок сохранён.",
            [Chunk(20, "Статья 12. Старый порядок.", isCurrent: false)]);

        result.AllConfirmed.ShouldBeFalse();
        result.Citations.ShouldContain(c => c.Status == CitationStatus.Superseded && c.SourceChunkId == 20);
    }
}
