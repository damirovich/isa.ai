using ISC.AI.Abstractions.Grounding;
using ISC.AI.Abstractions.Retrieval;

namespace ISC.AI.AI.Grounding;

/// <summary>
/// Нейтральная реализация грунтовки (ТБ-040, ADR-0005/0008/0013). Извлекает ссылки из вывода
/// (доменный <see cref="ICitationExtractor"/>), нормализует их и фрагменты (доменный
/// <see cref="ICitationNormalizer"/>) и классифицирует каждую ссылку относительно ФАКТИЧЕСКИ
/// извлечённых фрагментов с обязательной проверкой годности источника (<c>IsCurrent</c>).
/// </summary>
/// <remarks>
/// Инвариант неотключаем и живёт в ядре: ссылка подтверждается ТОЛЬКО если сопоставлена с АКТУАЛЬНЫМ
/// фрагментом; найденная лишь в неактуальном — <see cref="CitationStatus.Superseded"/>; не найденная —
/// <see cref="CitationStatus.Unverified"/>. Профиль поставляет лишь доменные извлечение/нормализацию,
/// но не может ослабить саму проверку (ТБ-041).
/// </remarks>
public sealed class GroundingValidator(ICitationExtractor extractor, ICitationNormalizer normalizer) : IGroundingValidator
{
    /// <inheritdoc />
    public GroundingResult Validate(string output, IReadOnlyList<RetrievedChunk> retrievedContext)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(retrievedContext);

        var citations = extractor.Extract(output);
        if (citations.Count == 0)
        {
            return new GroundingResult([], AllConfirmed: true); // ссылок нет — проверять нечего
        }

        // Нормализованный текст фрагментов, раздельно: актуальные и утратившие силу.
        var current = NormalizeFragments(retrievedContext, isCurrent: true);
        var stale = NormalizeFragments(retrievedContext, isCurrent: false);

        var checks = new List<CitationCheck>(citations.Count);
        foreach (var citation in citations)
        {
            var normalized = normalizer.Normalize(citation);

            if (string.IsNullOrWhiteSpace(normalized))
            {
                checks.Add(new CitationCheck(citation, CitationStatus.Unverified));
            }
            else if (TryMatch(current, normalized, out var currentChunkId))
            {
                checks.Add(new CitationCheck(citation, CitationStatus.Confirmed, currentChunkId));
            }
            else if (TryMatch(stale, normalized, out var staleChunkId))
            {
                // Источник найден, но не актуален — не выдавать как действующую (ADR-0013, ТЭ-003).
                checks.Add(new CitationCheck(citation, CitationStatus.Superseded, staleChunkId));
            }
            else
            {
                checks.Add(new CitationCheck(citation, CitationStatus.Unverified)); // нет фрагмента — возможна галлюцинация
            }
        }

        var allConfirmed = checks.TrueForAll(c => c.Status == CitationStatus.Confirmed);
        return new GroundingResult(checks, allConfirmed);
    }

    private List<(int ChunkId, string Text)> NormalizeFragments(IReadOnlyList<RetrievedChunk> context, bool isCurrent)
    {
        var result = new List<(int, string)>();
        foreach (var chunk in context)
        {
            if (chunk.IsCurrent == isCurrent)
            {
                result.Add((chunk.ChunkId, normalizer.Normalize(chunk.Text)));
            }
        }

        return result;
    }

    private static bool TryMatch(List<(int ChunkId, string Text)> fragments, string normalizedCitation, out int chunkId)
    {
        foreach (var (id, text) in fragments)
        {
            if (text.Contains(normalizedCitation, StringComparison.Ordinal))
            {
                chunkId = id;
                return true;
            }
        }

        chunkId = 0;
        return false;
    }
}
