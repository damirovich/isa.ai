namespace ISC.AI.Persistence.Entities;

/// <summary>
/// Пользователь системы — субъект доступа. Схема <c>core</c>. Допуск (<see cref="ClearanceEntity"/>) —
/// обязательный вход решётки доступа (ТБ-002, ТБ-011, ТБ-012). Мягко удаляемый (история сохраняется).
/// </summary>
public class AppUserEntity : SoftDeletableEntity
{
    /// <summary>Уникальное имя входа.</summary>
    public required string UserName { get; set; }

    /// <summary>
    /// Идентификатор учётки во внешней системе идентификации (Э3-08) — привязка по значению, без FK
    /// через границу систем (ТО-инф-06). <c>null</c> — локальная учётка без внешней привязки.
    /// </summary>
    public string? ExternalId { get; set; }

    /// <summary>Отображаемое имя.</summary>
    public string? DisplayName { get; set; }

    /// <summary>Активен ли пользователь (немедленный отзыв — ТБ-016).</summary>
    public bool IsActive { get; set; } = true;

    /// <summary>Допуск пользователя.</summary>
    public ClearanceEntity? Clearance { get; set; }
}
