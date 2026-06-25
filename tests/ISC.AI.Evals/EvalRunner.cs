using ISC.AI.Abstractions.Retrieval;
using ISC.AI.Abstractions.Security;

namespace ISC.AI.Evals;

/// <summary>
/// Прогон эталонного набора через ядровой <see cref="IRetriever"/> и расчёт recall@k раздельно по
/// языку (Э4-05, КИ-03). Реальные числа получаются на загруженном корпусе + курированном эталоне.
/// </summary>
public sealed class EvalRunner(IRetriever retriever)
{
    /// <summary>Для каждого случая извлекает топ-k и считает recall; агрегирует раздельно по языку.</summary>
    public async Task<IReadOnlyList<LanguageRecall>> RunRecallAsync(
        IReadOnlyList<EvalCase> cases, AccessContext access, int k, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cases);

        var caseRecalls = new List<(string Language, double Recall)>(cases.Count);
        foreach (var evalCase in cases)
        {
            var retrieved = await retriever.RetrieveAsync(evalCase.Query, access, k, cancellationToken: cancellationToken);
            var retrievedIds = retrieved.Select(chunk => chunk.ChunkId).ToList();
            caseRecalls.Add((evalCase.Language, RecallCalculator.CaseRecall(retrievedIds, evalCase.RelevantChunkIds)));
        }

        return RecallCalculator.ByLanguage(caseRecalls);
    }
}
