using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Profile.Inspector.Domain.Services;
using Mediator;

namespace ISC.AI.Profile.Inspector.Application.Features.Risks;

/// <summary>
/// Разбор риска по подразделениям (§5.2.5.1) за последние <paramref name="PeriodDays"/> дней:
/// сигналы формулы по отдельности, болевые сферы и балл. Балл — детерминированный код
/// (Приложение §2), не ИИ; данные — живой учёт нарушений (Э5-01).
/// </summary>
public sealed record GetDivisionRisksQuery(int PeriodDays = 90)
    : IRequest<ResponseDto<IReadOnlyList<DivisionRiskDetail>>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.View;

    /// <inheritdoc />
    public string? AuditSummary => $"inspector:risks:view:days={PeriodDays}";

    /// <inheritdoc cref="GetDivisionRisksQuery" />
    public sealed class Handler(IRiskDataSource riskDataSource)
        : IRequestHandler<GetDivisionRisksQuery, ResponseDto<IReadOnlyList<DivisionRiskDetail>>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<IReadOnlyList<DivisionRiskDetail>>> Handle(
            GetDivisionRisksQuery query, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(query);

            var days = Math.Clamp(query.PeriodDays, 7, 366);
            var to = DateOnly.FromDateTime(DateTime.UtcNow);
            var risks = await riskDataSource.GetDivisionRisksAsync(to.AddDays(-(days - 1)), to, cancellationToken);
            return ResponseDto<IReadOnlyList<DivisionRiskDetail>>.Ok(risks);
        }
    }
}
