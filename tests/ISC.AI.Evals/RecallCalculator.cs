namespace ISC.AI.Evals;

/// <summary>
/// Расчёт recall@k (КИ-03). Ключевое требование Э4-05: агрегировать **раздельно по языку**, чтобы
/// провал кыргызского НЕ маскировался средним по русскому.
/// </summary>
public static class RecallCalculator
{
    /// <summary>
    /// Recall одного случая: доля ожидаемо релевантных фрагментов, попавших в топ-k извлечения.
    /// Пустой эталонный набор трактуется как 1.0 (искать нечего).
    /// </summary>
    public static double CaseRecall(IReadOnlyCollection<int> retrievedTopK, IReadOnlyCollection<int> relevant)
    {
        ArgumentNullException.ThrowIfNull(retrievedTopK);
        ArgumentNullException.ThrowIfNull(relevant);

        if (relevant.Count == 0)
        {
            return 1.0;
        }

        var hits = relevant.Count(retrievedTopK.Contains);
        return (double)hits / relevant.Count;
    }

    /// <summary>
    /// Агрегирует recall случаев ОТДЕЛЬНО по каждому языку (КИ-03) — без межъязыкового усреднения.
    /// </summary>
    public static IReadOnlyList<LanguageRecall> ByLanguage(IEnumerable<(string Language, double Recall)> caseRecalls)
    {
        ArgumentNullException.ThrowIfNull(caseRecalls);

        return caseRecalls
            .GroupBy(item => item.Language, StringComparer.OrdinalIgnoreCase)
            .Select(group => new LanguageRecall(group.Key, group.Average(item => item.Recall), group.Count()))
            .OrderBy(result => result.Language, StringComparer.Ordinal)
            .ToList();
    }
}
