using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.Admin.Domain.Services;
using Mediator;

namespace ISC.AI.Modules.Admin.Application.Features.Sessions;

/// <summary>Строка экрана «Активные сеансы».</summary>
/// <param name="SessionId">Номер сеанса.</param>
/// <param name="UserId">Сотрудник.</param>
/// <param name="UserName">Имя входа.</param>
/// <param name="DisplayName">ФИО.</param>
/// <param name="ClientAddress">IP-адрес.</param>
/// <param name="UserAgent">Полная строка браузера — для подсказки.</param>
/// <param name="Workstation">Рабочее место словами: браузер и система.</param>
/// <param name="StartedAtUtc">Вход, UTC.</param>
/// <param name="LastActivityAtUtc">Последняя активность, UTC.</param>
/// <param name="Connected">Открыта вкладка — живое соединение с сервером.</param>
/// <param name="IsCurrent">Это сеанс того, кто смотрит список: завершать его нужно «Выходом».</param>
public sealed record ActiveSessionRow(
    Guid SessionId,
    int UserId,
    string UserName,
    string? DisplayName,
    string? ClientAddress,
    string? UserAgent,
    string Workstation,
    DateTime StartedAtUtc,
    DateTime LastActivityAtUtc,
    bool Connected,
    bool IsCurrent);

/// <summary>Экран «Активные сеансы»: строки и таймаут неактивности для пояснения.</summary>
/// <param name="Rows">Открытые сеансы, свежие сверху.</param>
/// <param name="IdleMinutes">Таймаут неактивности, минут.</param>
public sealed record ActiveSessionsView(IReadOnlyList<ActiveSessionRow> Rows, int IdleMinutes);

/// <summary>
/// Активные сеансы (ТФ-АДМ-09, ADR-0060): кто сейчас работает в системе — не завершённые сеансы, у которых открыта
/// вкладка или была активность в пределах таймаута неактивности.
/// </summary>
/// <remarks>
/// ИНВАРИАНТ (ТБ-012): право смотреть и завершать сеансы знает профиль (<see cref="IPlatformAdministration.CanManageSessionsAsync"/>).
/// Открытие списка — запись <c>View</c> в журнале (ТБ-030). Сеансы не режимные данные (гриф и подразделение у них
/// нет): список показывает всех вошедших — смысл экрана в том, чтобы видеть, кто в системе.
/// </remarks>
/// <param name="Text">Отбор по имени входа, ФИО и IP-адресу.</param>
public sealed record ListActiveSessionsQuery(string? Text = null) : IRequest<ResponseDto<ActiveSessionsView>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.View;

    /// <inheritdoc />
    public string? AuditSummary => "admin:user-sessions:view";

    /// <inheritdoc cref="ListActiveSessionsQuery" />
    public sealed class Handler(
        IUserSessionStore store,
        IUserSessionMonitor monitor,
        IPlatformAdministration administration,
        ISubjectProvider subjectProvider,
        TimeProvider timeProvider)
        : IRequestHandler<ListActiveSessionsQuery, ResponseDto<ActiveSessionsView>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<ActiveSessionsView>> Handle(ListActiveSessionsQuery query, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(query);

            if (!await administration.CanManageSessionsAsync(cancellationToken))
            {
                return ResponseDto<ActiveSessionsView>.BadRequest(AdminGuard.SessionsDenied);
            }

            var connected = monitor.Connected;
            var since = timeProvider.GetUtcNow().UtcDateTime - monitor.IdleTimeout;
            var rows = await store.ListOpenAsync(since, connected, query.Text, cancellationToken);
            var live = connected.ToHashSet();
            var current = await subjectProvider.GetCurrentSessionIdAsync(cancellationToken);

            return ResponseDto<ActiveSessionsView>.Ok(new ActiveSessionsView(
                rows.Select(r => new ActiveSessionRow(
                        r.SessionId, r.UserId, r.UserName, r.DisplayName, r.ClientAddress, r.UserAgent,
                        WorkstationLabel.Describe(r.UserAgent), r.StartedAtUtc, r.LastActivityAtUtc, live.Contains(r.SessionId),
                        r.SessionId == current))
                    .ToList(),
                (int)monitor.IdleTimeout.TotalMinutes));
        }
    }
}
