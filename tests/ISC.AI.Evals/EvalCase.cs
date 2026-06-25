namespace ISC.AI.Evals;

/// <summary>
/// Эталонный случай оценки RAG (Э4-05): запрос, язык и ожидаемо релевантные фрагменты.
/// Раздельность по языку — обязательна (КИ-03): метрики считаются отдельно для ru и ky.
/// </summary>
/// <param name="Query">Запрос инспектора.</param>
/// <param name="Language">Язык запроса (например, «ru» / «ky»).</param>
/// <param name="RelevantChunkIds">Идентификаторы фрагментов, которые должны быть найдены.</param>
public sealed record EvalCase(string Query, string Language, IReadOnlyCollection<int> RelevantChunkIds);
