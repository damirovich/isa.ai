namespace ISC.AI.Abstractions.Ingestion;

/// <summary>
/// Разбиение текста документа на фрагменты-чанки для индексации (ТО-мат-03). Стратегия чанкинга
/// влияет на качество извлечения; реализация по умолчанию — в ядре, может быть заменена.
/// </summary>
public interface ITextChunker
{
    /// <summary>Возвращает упорядоченные непустые фрагменты текста.</summary>
    IReadOnlyList<string> Chunk(string text);
}
