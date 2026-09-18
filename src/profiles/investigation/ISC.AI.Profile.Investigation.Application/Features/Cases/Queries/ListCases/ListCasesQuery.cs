using System.Globalization;
using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Investigation.Domain.Enums;
using ISC.AI.Profile.Investigation.Domain.Services;
using Mediator;

namespace ISC.AI.Profile.Investigation.Application.Features.Cases;

/// <summary>
/// Список дел субъекта (ТФ-ДЕЛ-03): следователь — свои дела, руководитель — дела подразделения; решётка
/// гриф/подразделение применяется хранилищем на стороне БД (ТБ-020).
/// </summary>
public sealed record ListCasesQuery(
    string? Text = null,
    CaseKind? Kind = null,
    CaseStatus? Status = null,
    int? DivisionId = null,
    int? InvestigatorUserId = null,
    int Page = 1,
    int PageSize = 25)
    : IRequest<ResponseDto<CasePage>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.View;

    /// <inheritdoc />
    /// <remarks>Только критерии отбора, без содержимого дел (ТБ-032); номер страницы в журнал не идёт.</remarks>
    public string? AuditSummary =>
        $"investigation:cases:list:text={(string.IsNullOrWhiteSpace(Text) ? "-" : "*")};kind={Kind?.ToString() ?? "-"};"
        + $"status={Status?.ToString() ?? "-"};division={DivisionId?.ToString(CultureInfo.InvariantCulture) ?? "-"};"
        + $"investigator={InvestigatorUserId?.ToString(CultureInfo.InvariantCulture) ?? "-"}";

    /// <inheritdoc cref="ListCasesQuery" />
    public sealed class Handler(ICaseStore cases, IAccessContextProvider accessProvider)
        : IRequestHandler<ListCasesQuery, ResponseDto<CasePage>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<CasePage>> Handle(ListCasesQuery query, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(query);

            // Fail-closed (ТБ-021): без контекста допуска GetCurrentAsync бросает — список не выдаётся вовсе.
            var access = await accessProvider.GetCurrentAsync(cancellationToken);
            var page = await cases.ListAsync(
                new CaseFilter(
                    query.Text, query.Kind, query.Status, query.DivisionId, query.InvestigatorUserId,
                    query.Page, query.PageSize),
                access,
                cancellationToken);
            return ResponseDto<CasePage>.Ok(page, page.TotalCount);
        }
    }
}
