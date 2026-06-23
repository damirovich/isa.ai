namespace ISC.AI.Profile.Inspector.Domain.Entities;

/// <summary>
/// Редакция нормы НПА (модель версионности профиля). По умолчанию извлекаются только действующие;
/// утратившая силу не выдаётся как действующая (ТО-инф-04, ТФ-НПА-02, контроль GATE-3). Схема
/// <c>inspector</c>. Доменный статус материализуется в нейтральный флаг <c>core.chunk.IsCurrent</c>
/// (по <see cref="ChunkRevisionLink"/>, в одной транзакции при смене статуса — ADR-0013, ДОК-04 §6.3).
/// </summary>
public class NormRevision : AuditableEntity
{
    /// <summary>Норма, к которой относится редакция (FK внутри схемы inspector).</summary>
    public int NormId { get; set; }

    /// <summary>Навигация к норме.</summary>
    public LegalNorm? Norm { get; set; }

    /// <summary>Статус редакции (ТО-инф-04, ТФ-НПА-02).</summary>
    public RevisionStatus Status { get; set; }

    /// <summary>Дата вступления в силу.</summary>
    public DateOnly EffectiveDate { get; set; }

    /// <summary>Дата утраты силы (если применимо).</summary>
    public DateOnly? RepealedDate { get; set; }
}
