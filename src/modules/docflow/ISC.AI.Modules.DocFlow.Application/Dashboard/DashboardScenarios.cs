using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.DocFlow.Domain.Services;
using Mediator;

namespace ISC.AI.Modules.DocFlow.Application.Dashboard;

/// <summary>
/// Сводка дашборда документооборота.
/// </summary>
/// <remarks>
/// НЕ помечается <c>IAuditableRequest</c> намеренно — по той же причине, что и лента уведомлений:
/// дашборд открывают при каждом входе, и запись о каждом таком открытии забивала бы неизменяемый
/// журнал шумом, в котором утонут настоящие обращения к документам. Сами документы читаются
/// отдельными сценариями — они аудируются.
/// </remarks>
public sealed record GetDocFlowDashboardQuery(int UpcomingCount = 10)
    : IRequest<ResponseDto<DashboardData>>
{
    /// <inheritdoc cref="GetDocFlowDashboardQuery" />
    public sealed class Handler(
        IDashboardDataSource dashboard,
        IAccessContextProvider accessProvider,
        IDocFlowClock clock,
        ISystemSettingsStore settings)
        : IRequestHandler<GetDocFlowDashboardQuery, ResponseDto<DashboardData>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<DashboardData>> Handle(
            GetDocFlowDashboardQuery query, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(query);

            // Fail-closed: без контекста допуска GetCurrentAsync бросает — сводка не строится вовсе.
            var access = await accessProvider.GetCurrentAsync(cancellationToken);

            // «Сегодня» — по часам ЭКСПЛУАТАНТА (Asia/Bishkek), не по UTC сервера: иначе вечером
            // местного времени «срок сегодня» уже относился бы к завтрашнему дню.
            var today = clock.Today;

            // Горизонт «скоро» — тот же, что у уведомлений о сроках (§9): на экране и в письме
            // «приближается» обязано означать одно и то же.
            var horizon = (await settings.GetAsync(cancellationToken)).NotificationHorizonDays;

            var data = await dashboard.GetAsync(
                access, today, horizon, query.UpcomingCount, cancellationToken);

            return ResponseDto<DashboardData>.Ok(data);
        }
    }
}
