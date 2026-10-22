namespace ISC.AI.Persistence.Entities;

/// <summary>
/// Сеанс входа (<c>core.user_session</c>, ТФ-АДМ-09, ADR-0060): кто, откуда, когда вошёл и когда был активен; по
/// завершении — когда, почему и кем закрыт. НЕ наследует <c>BaseEntity</c>: ключ — случайный номер сеанса, который
/// уходит в cookie входа; строки не удаляются мягко — это история сеансов.
/// </summary>
public class UserSessionEntity
{
    /// <summary>Номер сеанса (случайный) — он же значение клейма <c>iscai:sid</c> в cookie входа.</summary>
    public Guid Id { get; set; }

    /// <summary>Сотрудник (<c>core.app_user</c>; внешний ключ без навигации — см. конфигурацию).</summary>
    public int UserId { get; set; }

    /// <summary>IP-адрес клиента; <see langword="null"/> — неизвестен.</summary>
    public string? ClientAddress { get; set; }

    /// <summary>Строка браузера (User-Agent), обрезанная до <see cref="MaxUserAgentLength"/>.</summary>
    public string? UserAgent { get; set; }

    /// <summary>Вход, UTC.</summary>
    public DateTime StartedAt { get; set; }

    /// <summary>Последняя активность, UTC.</summary>
    public DateTime LastActivityAt { get; set; }

    /// <summary>Сеанс закрыт, UTC; <see langword="null"/> — действует.</summary>
    public DateTime? EndedAt { get; set; }

    /// <summary>Почему закрыт.</summary>
    public UserSessionEndReason? EndReason { get; set; }

    /// <summary>Кто закрыл (Администратор или Офицер ИБ) — для завершения с экрана.</summary>
    public int? EndedByUserId { get; set; }

    /// <summary>Пояснение к закрытию (причина, указанная при завершении).</summary>
    public string? EndComment { get; set; }

    /// <summary>Предел длины строки браузера.</summary>
    public const int MaxUserAgentLength = 300;

    /// <summary>Предел длины IP-адреса (IPv6 с зоной).</summary>
    public const int MaxClientAddressLength = 64;

    /// <summary>Предел длины пояснения к закрытию.</summary>
    public const int MaxEndCommentLength = 500;
}
