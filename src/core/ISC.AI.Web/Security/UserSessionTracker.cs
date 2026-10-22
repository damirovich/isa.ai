using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Claims;
using ISC.AI.Abstractions.Security;

namespace ISC.AI.Web.Security;

/// <summary>
/// Сеансы входа на стороне хоста (ТФ-АДМ-09, ADR-0060): заведение и закрытие сеанса, проверка «сеанс действует» с
/// коротким кэшем, отметка активности не чаще раза в минуту и реестр живых соединений Blazor с мгновенным выходом.
/// Один экземпляр на процесс.
/// </summary>
/// <remarks>
/// ИНВАРИАНТ (ТБ-014/016): завершённый сеанс не пускает дальше. Проверка на каждом HTTP-запросе спрашивает базу не
/// чаще раза в <see cref="StatusCacheTime"/>; завершение в ЭТОМ процессе сбрасывает кэш сразу и переводит живые
/// соединения сеанса в «не вошёл». Сбой записи активности не роняет запрос пользователя — он пишется в лог.
/// </remarks>
public sealed partial class UserSessionTracker(
    IUserSessionStore store,
    IConfiguration configuration,
    TimeProvider timeProvider,
    ILogger<UserSessionTracker> logger) : IUserSessionMonitor
{
    /// <summary>Сколько держать в памяти ответ «сеанс действует» до повторного вопроса к базе.</summary>
    public static readonly TimeSpan StatusCacheTime = TimeSpan.FromSeconds(15);

    /// <summary>Как часто отмечать активность сеанса в базе.</summary>
    public static readonly TimeSpan ActivityInterval = TimeSpan.FromMinutes(1);

    // Предел записей кэша: сеансов в сутки сотни, но долгий аптайм не должен копить словарь без конца.
    private const int MaxCachedSessions = 10_000;

    private readonly ConcurrentDictionary<Guid, (bool Active, DateTimeOffset CheckedAt)> _status = new();
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _touched = new();
    private readonly ConcurrentDictionary<object, CircuitEntry> _circuits = new();

    /// <inheritdoc />
    public TimeSpan IdleTimeout { get; } = TimeSpan.FromMinutes(
        int.TryParse(configuration["Auth:SessionIdleMinutes"], NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes)
        && minutes > 0 ? minutes : 30);

    /// <inheritdoc />
    public IReadOnlyCollection<Guid> Connected =>
        _circuits.Values.Where(c => c.Connected).Select(c => c.SessionId).Distinct().ToList();

    /// <summary>Номер сеанса из принципала (клейм <see cref="UserSessionClaims.SessionId"/>); нет или не номер — <see langword="null"/>.</summary>
    public static Guid? SessionIdOf(ClaimsPrincipal? principal) =>
        Guid.TryParse(principal?.FindFirst(UserSessionClaims.SessionId)?.Value, out var id) ? id : null;

    /// <summary>Заводит сеанс при входе.</summary>
    public async Task<Guid> StartAsync(int userId, string? clientAddress, string? userAgent, CancellationToken cancellationToken)
    {
        var sessionId = await store.StartAsync(new UserSessionStart(userId, clientAddress, userAgent), cancellationToken);
        var now = timeProvider.GetUtcNow();
        _status[sessionId] = (true, now);
        _touched[sessionId] = now;
        return sessionId;
    }

    /// <summary>Действует ли сеанс — с кэшем на <see cref="StatusCacheTime"/>.</summary>
    public async Task<bool> IsActiveAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        if (_status.TryGetValue(sessionId, out var cached) && (!cached.Active || now - cached.CheckedAt < StatusCacheTime))
        {
            // Завершённый сеанс не оживает — его ответ кэшируется бессрочно.
            return cached.Active;
        }

        var active = await store.IsActiveAsync(sessionId, cancellationToken);
        Remember(_status, sessionId, (active, now));
        return active;
    }

    /// <summary>Отмечает активность сеанса — в базе не чаще раза в <see cref="ActivityInterval"/>.</summary>
    public async Task NoteActivityAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        if (_touched.TryGetValue(sessionId, out var last) && now - last < ActivityInterval)
        {
            return;
        }

        Remember(_touched, sessionId, now);
        try
        {
            await store.TouchAsync(sessionId, now.UtcDateTime, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Отметка активности — сведения для экрана, не решение о доступе: её сбой не должен ронять работу.
            TouchFailed(logger, ex);
        }
    }

    /// <summary>Закрывает сеанс и сразу выводит его соединения на форму входа.</summary>
    /// <returns><see langword="true"/> — сеанс закрыт этим вызовом.</returns>
    public async Task<bool> EndAsync(Guid sessionId, UserSessionEndReason reason, CancellationToken cancellationToken)
    {
        var ended = await store.EndAsync(sessionId, reason, endedByUserId: null, comment: null, cancellationToken);
        SignalEnded(sessionId);
        return ended;
    }

    /// <inheritdoc />
    public void SignalEnded(Guid sessionId)
    {
        _status[sessionId] = (false, timeProvider.GetUtcNow());
        _touched.TryRemove(sessionId, out _);

        foreach (var circuit in _circuits.Values.Where(c => c.SessionId == sessionId))
        {
            try
            {
                circuit.SignOut();
            }
            catch (Exception ex)
            {
                // Одно сломанное соединение не мешает погасить остальные; запрос с его cookie всё равно отклонится.
                SignOutFailed(logger, ex);
            }
        }
    }

    /// <summary>Соединение Blazor открыто: запомнить сеанс и как перевести соединение в «не вошёл».</summary>
    /// <param name="circuit">Ключ соединения (обработчик соединения).</param>
    /// <param name="sessionId">Сеанс.</param>
    /// <param name="signOut">Перевести соединение в «не вошёл» — вызывается с любого потока.</param>
    public void RegisterCircuit(object circuit, Guid sessionId, Action signOut) =>
        _circuits[circuit] = new CircuitEntry(sessionId, signOut) { Connected = true };

    /// <summary>Связь соединения с браузером появилась или пропала («на связи» на экране).</summary>
    public void SetCircuitConnected(object circuit, bool connected)
    {
        if (_circuits.TryGetValue(circuit, out var entry))
        {
            entry.Connected = connected;
        }
    }

    /// <summary>Соединение закрыто.</summary>
    public void UnregisterCircuit(object circuit) => _circuits.TryRemove(circuit, out _);

    private static void Remember<T>(ConcurrentDictionary<Guid, T> map, Guid key, T value)
    {
        if (map.Count >= MaxCachedSessions)
        {
            map.Clear();
        }

        map[key] = value;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Сеансы входа: не удалось отметить активность сеанса — доступ не затронут")]
    private static partial void TouchFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Сеансы входа: не удалось перевести соединение завершённого сеанса на форму входа")]
    private static partial void SignOutFailed(ILogger logger, Exception exception);

    private sealed class CircuitEntry(Guid sessionId, Action signOut)
    {
        public Guid SessionId { get; } = sessionId;

        public Action SignOut { get; } = signOut;

        public volatile bool Connected;
    }
}
