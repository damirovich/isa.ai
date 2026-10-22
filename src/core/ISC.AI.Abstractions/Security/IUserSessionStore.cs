namespace ISC.AI.Abstractions.Security;

/// <summary>Почему сеанс закрыт (ADR-0060).</summary>
public enum UserSessionEndReason : short
{
    /// <summary>Сотрудник вышел сам («Выход»).</summary>
    SignedOut = 1,

    /// <summary>Сеанс завершил Администратор или Офицер ИБ — с причиной (ТФ-АДМ-09).</summary>
    Terminated = 2,

    /// <summary>Доступ отозван: учётная запись отключена или штамп безопасности разошёлся (ТБ-016).</summary>
    Revoked = 3,
}

/// <summary>Начало сеанса: кто вошёл и откуда.</summary>
/// <param name="UserId">Сотрудник (<c>core.app_user</c>).</param>
/// <param name="ClientAddress">IP-адрес клиента; <see langword="null"/> — неизвестен.</param>
/// <param name="UserAgent">Строка браузера (User-Agent); <see langword="null"/> — не передана.</param>
public sealed record UserSessionStart(int UserId, string? ClientAddress, string? UserAgent);

/// <summary>Открытый сеанс для экрана «Активные сеансы».</summary>
/// <param name="SessionId">Номер сеанса.</param>
/// <param name="UserId">Сотрудник.</param>
/// <param name="UserName">Имя входа.</param>
/// <param name="DisplayName">ФИО; <see langword="null"/> — не заполнено.</param>
/// <param name="ClientAddress">IP-адрес клиента.</param>
/// <param name="UserAgent">Строка браузера.</param>
/// <param name="StartedAtUtc">Вход, UTC.</param>
/// <param name="LastActivityAtUtc">Последняя активность, UTC.</param>
public sealed record UserSessionRow(
    Guid SessionId,
    int UserId,
    string UserName,
    string? DisplayName,
    string? ClientAddress,
    string? UserAgent,
    DateTime StartedAtUtc,
    DateTime LastActivityAtUtc);

/// <summary>
/// Учёт сеансов входа (<c>core.user_session</c>, ТФ-АДМ-09, ADR-0060): начало при входе, отметка активности, проверка
/// «сеанс действует», завершение, список открытых.
/// </summary>
/// <remarks>
/// ИНВАРИАНТЫ (ТБ-014/016). (1) Завершённый сеанс не оживает: завершить можно только действующий, повтор ничего не
/// меняет, а проверка хоста гасит cookie завершённого сеанса на следующем запросе. (2) Строки не удаляются — это
/// история сеансов. (3) Порт профиле-нейтрален: кто вправе смотреть и завершать сеансы — решает профиль.
/// </remarks>
public interface IUserSessionStore
{
    /// <summary>Заводит сеанс при входе; время входа и последней активности — сейчас.</summary>
    /// <returns>Номер сеанса — случайный, кладётся в cookie входа.</returns>
    Task<Guid> StartAsync(UserSessionStart start, CancellationToken cancellationToken = default);

    /// <summary>Действует ли сеанс: есть и не завершён. Неизвестный номер — <see langword="false"/>.</summary>
    Task<bool> IsActiveAsync(Guid sessionId, CancellationToken cancellationToken = default);

    /// <summary>Отмечает активность действующего сеанса (завершённый не трогается).</summary>
    Task TouchAsync(Guid sessionId, DateTime atUtc, CancellationToken cancellationToken = default);

    /// <summary>Завершает ДЕЙСТВУЮЩИЙ сеанс.</summary>
    /// <param name="sessionId">Сеанс.</param>
    /// <param name="reason">Почему.</param>
    /// <param name="endedByUserId">Кто завершил (для <see cref="UserSessionEndReason.Terminated"/>).</param>
    /// <param name="comment">Пояснение (причина завершения администратором).</param>
    /// <param name="cancellationToken">Отмена.</param>
    /// <returns><see langword="true"/> — сеанс завершён этим вызовом; <see langword="false"/> — его нет или он уже закрыт.</returns>
    Task<bool> EndAsync(
        Guid sessionId, UserSessionEndReason reason, int? endedByUserId, string? comment, CancellationToken cancellationToken = default);

    /// <summary>
    /// Открытые сеансы: не завершены и либо активны не раньше <paramref name="activeSinceUtc"/>, либо входят в
    /// <paramref name="connected"/> (у них живое соединение). Отбор <paramref name="text"/> — по имени входа, ФИО и
    /// IP-адресу без учёта регистра. Порядок — по последней активности, свежие сверху.
    /// </summary>
    Task<IReadOnlyList<UserSessionRow>> ListOpenAsync(
        DateTime activeSinceUtc, IReadOnlyCollection<Guid> connected, string? text, CancellationToken cancellationToken = default);
}

/// <summary>
/// Живые сеансы процесса хоста (ADR-0060): какие сеансы сейчас на связи (открыто соединение Blazor) и мгновенное
/// завершение их соединений. Реализует хост — только он знает соединения.
/// </summary>
public interface IUserSessionMonitor
{
    /// <summary>Таймаут неактивности: сеанс без активности дольше и без соединения активным не считается.</summary>
    TimeSpan IdleTimeout { get; }

    /// <summary>Сеансы, у которых сейчас есть живое соединение в этом процессе.</summary>
    IReadOnlyCollection<Guid> Connected { get; }

    /// <summary>
    /// Сеанс завершён: его соединения немедленно переводятся в «не вошёл» (страница уходит на форму входа), а
    /// закэшированное состояние сеанса сбрасывается — следующий HTTP-запрос с его cookie отклоняется.
    /// </summary>
    void SignalEnded(Guid sessionId);
}

/// <summary>Клейм сеанса в cookie входа (ADR-0060): общий для хоста, который его кладёт, и пакетов, которые его читают.</summary>
public static class UserSessionClaims
{
    /// <summary>Номер сеанса (<see cref="IUserSessionStore"/>).</summary>
    public const string SessionId = "iscai:sid";
}
