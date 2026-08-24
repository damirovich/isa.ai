using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Profile.Inspector.Domain.Enums;
using ISC.AI.Profile.Inspector.Domain.Services;
using Mediator;

namespace ISC.AI.Profile.Inspector.Application.Features.Violations;

/// <summary>Страница реестра нарушений с отбором (Э5-01). Читают все вошедшие — данные без грифа.</summary>
public sealed record ListViolationsQuery(
    int? DivisionId = null,
    int? CategoryId = null,
    ViolationSeverity? Severity = null,
    RemediationStatus? RemediationStatus = null,
    DateOnly? DetectedFrom = null,
    DateOnly? DetectedTo = null,
    int Page = 1,
    int PageSize = 25) : IRequest<ResponseDto<ViolationPage>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.Search;

    /// <inheritdoc />
    public string? AuditSummary =>
        $"inspector:violations:list:division={DivisionId};category={CategoryId};severity={Severity};page={Page}";

    /// <inheritdoc cref="ListViolationsQuery" />
    public sealed class Handler(IViolationStore store) : IRequestHandler<ListViolationsQuery, ResponseDto<ViolationPage>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<ViolationPage>> Handle(
            ListViolationsQuery query, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(query);
            var page = await store.ListAsync(
                new ViolationListFilter(
                    query.DivisionId, query.CategoryId, query.Severity, query.RemediationStatus,
                    query.DetectedFrom, query.DetectedTo, query.Page, query.PageSize),
                cancellationToken);
            return ResponseDto<ViolationPage>.Ok(page);
        }
    }
}
