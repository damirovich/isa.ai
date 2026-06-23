namespace ISC.AI.Profile.Inspector.Domain.Entities;

/// <summary>
/// Связка «редакция НПА ↔ чанк корпуса ядра». Слабая ссылка по значению <see cref="ChunkId"/> на
/// <c>core.chunk.Id</c> БЕЗ FK через границу схем (ТО-инф-06); <see cref="NormRevisionId"/> — FK
/// внутри схемы <c>inspector</c>. При смене статуса редакции профиль по этой связке материализует
/// <c>core.chunk.IsCurrent</c> (ADR-0013, ДОК-04 §6.3). При удалении чанка в ядре (ТБ-064) профиль
/// удаляет осиротевшие связки в той же транзакции.
/// </summary>
public class ChunkRevisionLink : AuditableEntity
{
    /// <summary>Редакция (FK внутри схемы inspector).</summary>
    public int NormRevisionId { get; set; }

    /// <summary>Навигация к редакции.</summary>
    public NormRevision? Revision { get; set; }

    /// <summary>Идентификатор чанка ядра (<c>core.chunk.Id</c>) — слабая ссылка по значению, без FK.</summary>
    public int ChunkId { get; set; }
}
