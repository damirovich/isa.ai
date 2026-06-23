namespace ISC.AI.Profile.Inspector.Domain.Entities;

/// <summary>
/// Связка «норма НПА ↔ документ корпуса ядра». Восстанавливает связь домена с нейтральным корпусом
/// БЕЗ FK через границу схем (ТО-инф-06): <see cref="DocumentId"/> — слабая ссылка по значению на
/// <c>core.document.Id</c>, а <see cref="LegalNormId"/> — настоящий FK внутри схемы <c>inspector</c>.
/// При гарантированном удалении документа в ядре (ТБ-064) профиль удаляет осиротевшие связки в той
/// же транзакции.
/// </summary>
public class NormDocumentLink : AuditableEntity
{
    /// <summary>Норма (FK внутри схемы inspector).</summary>
    public int LegalNormId { get; set; }

    /// <summary>Навигация к норме.</summary>
    public LegalNorm? LegalNorm { get; set; }

    /// <summary>Идентификатор документа ядра (<c>core.document.Id</c>) — слабая ссылка по значению, без FK.</summary>
    public int DocumentId { get; set; }
}
