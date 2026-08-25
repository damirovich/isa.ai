using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Profile.Inspector.Domain.Services;
using Mediator;

namespace ISC.AI.Profile.Inspector.Application.Features.Dashboard;

/// <summary>
/// Сводка дашборда (§5.2.5) за последние <paramref name="PeriodDays"/> дней с отбором ТФ-ДШ-02
/// (подразделение, вид — сфера включает её виды, тяжесть, статус): счётчики нарушений
/// и светофор риска по подразделениям. Балл — детерминированный код (Приложение §2), не ИИ.
/// </summary>
public sealed record GetDashboardQuery(
    int PeriodDays = 90,
    int? DivisionId = null,
    int? CategoryId = null,
    Domain.Enums.ViolationSeverity? Severity = null,
    Domain.Enums.RemediationStatus? RemediationStatus = null)
    : IRequest<ResponseDto<DashboardSummary>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.View;

    /// <inheritdoc />
    public string? AuditSummary => $"inspector:dashboard:view:days={PeriodDays}"
        + (DivisionId is { } d ? $":division={d}" : string.Empty);

    /// <inheritdoc cref="GetDashboardQuery" />
    public sealed class Handler(IRiskDataSource riskDataSource)
        : IRequestHandler<GetDashboardQuery, ResponseDto<DashboardSummary>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<DashboardSummary>> Handle(
            GetDashboardQuery query, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(query);

            var days = Math.Clamp(query.PeriodDays, 7, 366);
            var to = DateOnly.FromDateTime(DateTime.UtcNow);
            var filter = new DashboardFilter(
                query.DivisionId, query.CategoryId, query.Severity, query.RemediationStatus);
            var summary = await riskDataSource.GetDashboardAsync(
                to.AddDays(-(days - 1)), to, filter, cancellationToken);
            return ResponseDto<DashboardSummary>.Ok(summary);
        }
    }
}
