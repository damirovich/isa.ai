namespace ISC.AI.Modules.DocFlow.Domain.Services;

/// <summary>Итог индексации документа в корпус ядра (этап 7 Э4-35).</summary>
public enum DocumentIndexStatus
{
    /// <summary>Проиндексирован (новая версия в корпусе; прежняя, если была, погашена supersede).</summary>
    Indexed,

    /// <summary>Текст не менялся — корпус уже актуален (дедуп ядра по хешу, ТНД-002).</summary>
    Unchanged,

    /// <summary>Документ документооборота не найден.</summary>
    NotFound,

    /// <summary>Конвейер ядра отказал (причина — в <see cref="DocumentIndexResult.Reason"/>).</summary>
    Rejected,
}

/// <summary>Результат индексации: статус + корпусный документ и число фрагментов при успехе.</summary>
public sealed record DocumentIndexResult(
    DocumentIndexStatus Status, int? CoreDocumentId = null, int ChunkCount = 0, string? Reason = null);

/// <summary>
/// Индексация документа документооборота в корпус ядра (ADR-0017 п.6): текст уходит в
/// <c>core.document</c>/<c>core.chunk</c> через конвейер загрузки (гриф и подразделение документа —
/// обязательные, fail-closed ТБ-024), связь ведёт мостик <c>document_index_link</c>. Выполняется
/// асинхронно очередью фоновых задач ядра — регистрация не ждёт эмбеддинги.
/// </summary>
public interface IDocumentIndexer
{
    /// <summary>Индексирует (или переиндексирует с гашением прежней версии) документ по идентификатору.</summary>
    Task<DocumentIndexResult> IndexAsync(int documentId, CancellationToken cancellationToken = default);
}
