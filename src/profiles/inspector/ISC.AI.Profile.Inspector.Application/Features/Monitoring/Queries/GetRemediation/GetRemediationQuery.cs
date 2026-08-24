using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Profile.Inspector.Domain.Services;
using Mediator;

namespace ISC.AI.Profile.Inspector.Application.Features.Monitoring;

/// <summary>
/// Сводка мониторинга устранения (§5.2.6) за последние <paramref name="PeriodDays"/> дней:
/// карточки нарушений (неустранённые первыми) и счётчики по подразделениям — из живого учёта (Э5-01).
/// </summary>
public sealed record GetRemediationQuery(int PeriodDays = 90)
    : IRequest<ResponseDto<RemediationSummary>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.View;

    /// <inheritdoc />
    public string? AuditSummary => $"inspector:monitoring:view:days={PeriodDays}";

    /// <inheritdoc cref="GetRemediationQuery" />
    public sealed class Handler(IRiskDataSource riskDataSource)
        : IRequestHandler<GetRemediationQuery, ResponseDto<RemediationSummary>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<RemediationSummary>> Handle(
            GetRemediationQuery query, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(query);

            var days = Math.Clamp(query.PeriodDays, 7, 366);
            var to = DateOnly.FromDateTime(DateTime.UtcNow);
            var summary = await riskDataSource.GetRemediationAsync(to.AddDays(-(days - 1)), to, cancellationToken);
            return ResponseDto<RemediationSummary>.Ok(summary);
        }
    }
}
