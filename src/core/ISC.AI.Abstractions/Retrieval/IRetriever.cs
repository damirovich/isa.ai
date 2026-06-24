using ISC.AI.Abstractions.Security;

namespace ISC.AI.Abstractions.Retrieval;

/// <summary>
/// Извлечение релевантных фрагментов корпуса для RAG (ТО-мат-01). Несущий режимный контроль.
/// </summary>
/// <remarks>
/// ИНВАРИАНТ БЕЗОПАСНОСТИ (ТБ-020): фильтрация по грифу и подразделению применяется НА СТОРОНЕ БД
/// ДО возврата результатов; материал выше допуска не возвращается ни в каком виде.
/// FAIL-CLOSED (ТБ-021/012): без контекста доступа извлечение не выполняется
/// (<see cref="AccessContextRequiredException"/>), а не выдаётся без фильтра.
/// НЕРАЗЛИЧИМОСТЬ (ТБ-022): наличие документов выше допуска не раскрывается через контент,
/// метаданные, ранжирование, текст/время ответа (методика GATE-1). Стратегия фильтрации
/// (pre/post-filter) — ADR-0007. По умолчанию возвращаются только актуальные источники.
/// </remarks>
public interface IRetriever
{
    /// <summary>
    /// Возвращает фрагменты, релевантные запросу и РАЗРЕШЁННЫЕ контексту доступа.
    /// </summary>
    /// <param name="query">Текст запроса для семантического поиска.</param>
    /// <param name="access">
    /// Контекст доступа субъекта. ОБЯЗАТЕЛЕН: при <see langword="null"/> — отказ
    /// (<see cref="AccessContextRequiredException"/>, fail-closed, ТБ-021).
    /// </param>
    /// <param name="topK">Желаемое число фрагментов (после применения фильтра доступа).</param>
    /// <param name="filter">Необязательные доменно-нейтральные ограничения; не ослабляют фильтр доступа.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    Task<IReadOnlyList<RetrievedChunk>> RetrieveAsync(
        string query,
        AccessContext access,
        int topK,
        RetrievalFilter? filter = null,
        CancellationToken cancellationToken = default);
}
