namespace ISC.AI.Abstractions.Retrieval;

/// <summary>
/// Извлечённый фрагмент корпуса с режимными метаданными (ТО-инф-03). Доменно-нейтрален: НПА-специфика
/// (идентификатор нормы, статус редакции) в ядровой контракт НЕ входит — она в расширяемых
/// <see cref="Metadata"/>; годность источника выражается нейтральным флагом <see cref="IsCurrent"/>
/// (его проверяет ядровая грунтовка, ADR-0013).
/// </summary>
/// <param name="ChunkId">Идентификатор фрагмента (<c>core.chunk.id</c>).</param>
/// <param name="DocumentId">Идентификатор документа-владельца (<c>core.document.id</c>).</param>
/// <param name="Text">Текст фрагмента.</param>
/// <param name="Classification">Гриф фрагмента (режим, ТБ-020).</param>
/// <param name="DivisionId">Подразделение фрагмента (режим, ТБ-020).</param>
/// <param name="IsCurrent">Флаг годности: источник актуален (не утратил силу).</param>
/// <param name="Score">Оценка релевантности (меньше — ближе; косинусное расстояние).</param>
/// <param name="Metadata">Доменный «багаж» профиля (для НПА — идентификатор нормы и статус редакции).</param>
public sealed record RetrievedChunk(
    int ChunkId,
    int DocumentId,
    string Text,
    short Classification,
    int DivisionId,
    bool IsCurrent,
    double Score,
    IReadOnlyDictionary<string, string>? Metadata = null);
