namespace ISC.AI.Persistence.Entities;

/// <summary>
/// Допуск пользователя — вход решётки доступа «гриф × роль × подразделение» (ТБ-002, ТБ-011/012).
/// Отзыв — мягкое удаление записи: глобальный фильтр скрывает её немедленно, и следующая операция
/// пользователя получает отказ fail-closed (ТБ-016, читается на каждую операцию, не кэшируется). Схема <c>core</c>.
/// </summary>
public class ClearanceEntity : SoftDeletableEntity
{
    /// <summary>Пользователь, которому выдан допуск.</summary>
    public int UserId { get; set; }

    /// <summary>Навигация к пользователю.</summary>
    public AppUserEntity? User { get; set; }

    /// <summary>Максимально доступный гриф (включительно). Фильтр доступа не выдаёт выше (ТБ-020).</summary>
    public short MaxClassification { get; set; }

    /// <summary>
    /// Разрешённые подразделения (идентификаторы <c>DivisionId</c> ресурсов корпуса) — источник
    /// <c>AccessContext.AllowedDivisions</c> для floor-фильтра (ТБ-002/020). Пустой список = доступ
    /// ни к одному подразделению (default-deny, ТБ-021), а не «ко всем».
    /// </summary>
    public List<int> DivisionScope { get; set; } = [];
}
