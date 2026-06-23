namespace ISC.AI.Profile.Inspector.Domain.Entities;

/// <summary>
/// Норма НПА (закон, приказ, положение и т. п.) с цепочкой редакций. Доменная сущность профиля
/// «ИнспекторAI» (схема <c>inspector</c>). Аудируемая и физически удаляемая (носитель ДСП — ТБ-064).
/// </summary>
public class LegalNorm : AuditableEntity
{
    /// <summary>Внешний идентификатор/номер НПА (на него ссылается грунтовка профиля).</summary>
    public required string Identifier { get; set; }

    /// <summary>Наименование нормы.</summary>
    public required string Title { get; set; }

    /// <summary>Редакции нормы (статусы «действующая/утратила силу»).</summary>
    public ICollection<NormRevision> Revisions { get; set; } = [];
}
