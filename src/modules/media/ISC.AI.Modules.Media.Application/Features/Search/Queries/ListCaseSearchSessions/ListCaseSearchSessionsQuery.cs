using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.Media.Domain.Model;
using ISC.AI.Modules.Media.Domain.Services;
using Mediator;

namespace ISC.AI.Modules.Media.Application.Features.Search;

/// <summary>История поисков дела (ТФ-ПЛ-07): сессии, новые первыми; только если дело доступно субъекту.</summary>
public sealed record ListCaseSearchSessionsQuery(int CaseId)
    : IRequest<ResponseDto<IReadOnlyList<SearchSessionRow>>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.View;

    /// <inheritdoc />
    public string? AuditSummary => $"media:case:{CaseId}:searches";

    /// <inheritdoc cref="ListCaseSearchSessionsQuery" />
    public sealed class Handler(IAccessContextProvider accessProvider, ICaseScope caseScope, ISearchSessionStore store)
        : IRequestHandler<ListCaseSearchSessionsQuery, ResponseDto<IReadOnlyList<SearchSessionRow>>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<IReadOnlyList<SearchSessionRow>>> Handle(
            ListCaseSearchSessionsQuery query, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(query);

            // Fail-closed (ТБ-020/021, ТБ-071): дело вне допуска/роли неотличимо от несуществующего.
            var access = await accessProvider.GetCurrentAsync(cancellationToken);
            var caseItem = await caseScope.GetCaseAsync(query.CaseId, access, cancellationToken);
            if (caseItem is null)
            {
                return ResponseDto<IReadOnlyList<SearchSessionRow>>.NotFound("Дело не найдено или недоступно.");
            }

            var sessions = await store.ListByCaseAsync(caseItem.CaseId, access, cancellationToken);
            return ResponseDto<IReadOnlyList<SearchSessionRow>>.Ok(sessions, sessions.Count);
        }
    }
}
