namespace ISC.AI.Abstractions.Corpus;

/// <summary>
/// Нейтральный порт ядра для материализации флага годности фрагментов (<c>core.chunk.IsCurrent</c>,
/// ADR-0013). Флагом владеет ЯДРО; КОГДА его менять — решает профиль по своей семантике (статус редакции
/// НПА и т. п.). Обновляет чанк и его эмбеддинг согласованно (опора фильтра актуальности GATE-3).
/// </summary>
public interface IChunkCurrencyPort
{
    /// <summary>Выставляет флаг годности у указанных чанков (и их эмбеддингов). Возвращает число затронутых чанков.</summary>
    Task<int> SetCurrencyAsync(IReadOnlyCollection<int> chunkIds, bool isCurrent, CancellationToken cancellationToken = default);
}
