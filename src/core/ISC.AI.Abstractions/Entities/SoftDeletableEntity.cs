namespace ISC.AI.Abstractions.Entities;

/// <summary>
/// Базовый класс сущности с мягким удалением: вместо физического DELETE выполняется UPDATE
/// с установкой <see cref="ISoftDeletable.IsDeleted"/> = <c>true</c>; в <c>OnModelCreating</c>
/// для всех <see cref="ISoftDeletable"/> регистрируется глобальный query-filter <c>!IsDeleted</c>.
/// </summary>
/// <remarks>
/// ВНИМАНИЕ (режим): НЕ применять к носителям ДСП, требующим гарантированного физического удаления
/// (документы/чанки/эмбеддинги — ТБ-064). Для них используется <see cref="AuditableEntity"/>.
/// </remarks>
public abstract class SoftDeletableEntity : AuditableEntity, ISoftDeletable
{
    /// <inheritdoc />
    public bool IsDeleted { get; set; }
}
