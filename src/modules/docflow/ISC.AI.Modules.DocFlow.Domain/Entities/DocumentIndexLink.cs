using ISC.AI.Abstractions.Entities;

namespace ISC.AI.Modules.DocFlow.Domain.Entities;

/// <summary>
/// Мостик «документ документооборота ↔ документ корпуса ядра» (Э4-35 этап 7, ADR-0017 п.6):
/// текст документа индексируется в <c>core.document</c>/<c>core.chunk</c> для RAG, и эта связь
/// позволяет ответу чата сослаться на конкретный документ/поручение и открыть его карточку.
/// </summary>
/// <remarks>
/// <see cref="CoreDocumentId"/> — СЛАБАЯ ссылка по значению через границу схем, БЕЗ FK (ТО-инф-06),
/// по образцу <c>inspector.norm_document_link</c>. Одна строка на документ: при переиндексации
/// (изменение текста) прежний корпусный документ гасится supersede-механикой ядра (Э4-14), а ссылка
/// обновляется на нового преемника.
/// </remarks>
public class DocumentIndexLink : AuditableEntity
{
    /// <summary>Документ документооборота (FK внутри схемы <c>docflow</c>).</summary>
    public int DocumentId { get; set; }

    /// <summary>Документ корпуса ядра (<c>core.document</c>) — слабая ссылка без FK (ТО-инф-06).</summary>
    public int CoreDocumentId { get; set; }

    /// <summary>Момент последней индексации (UTC) — диагностика отставания корпуса от домена.</summary>
    public DateTime IndexedAt { get; set; }
}
