namespace ISC.AI.Persistence.Entities;

/// <summary>
/// Пользователь системы — субъект доступа. Схема <c>core</c>. Допуск (<see cref="ClearanceEntity"/>) —
/// обязательный вход решётки доступа (ТБ-002, ТБ-011, ТБ-012). Мягко удаляемый (история сохраняется).
/// </summary>
public class AppUserEntity : SoftDeletableEntity
{
    /// <summary>Уникальное имя входа.</summary>
    public required string UserName { get; set; }

    /// <summary>Отображаемое имя.</summary>
    public string? DisplayName { get; set; }

    /// <summary>Активен ли пользователь (немедленный отзыв — ТБ-016).</summary>
    public bool IsActive { get; set; } = true;

    /// <summary>Допуск пользователя.</summary>
    public ClearanceEntity? Clearance { get; set; }
}
