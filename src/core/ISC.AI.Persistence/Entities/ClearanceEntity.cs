namespace ISC.AI.Persistence.Entities;

/// <summary>
/// Допуск пользователя — вход решётки доступа «гриф × роль × подразделение» (ТБ-002, ТБ-011/012).
/// Детализация по подразделениям и немедленный отзыв — Э3-08 (ТБ-015/016). Схема <c>core</c>.
/// </summary>
public class ClearanceEntity : SoftDeletableEntity
{
    /// <summary>Пользователь, которому выдан допуск.</summary>
    public int UserId { get; set; }

    /// <summary>Навигация к пользователю.</summary>
    public AppUserEntity? User { get; set; }

    /// <summary>Максимально доступный гриф (включительно). Фильтр доступа не выдаёт выше (ТБ-020).</summary>
    public short MaxClassification { get; set; }
}
