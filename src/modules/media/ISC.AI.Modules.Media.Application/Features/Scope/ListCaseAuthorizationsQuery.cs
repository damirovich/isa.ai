using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.Media.Domain.Services;
using Mediator;

namespace ISC.AI.Modules.Media.Application.Features.Scope;

/// <summary>
/// Основания поиска дела (поручения, постановления, ОРМ — ТБ-071): без основания поиск по лицу
/// не запускается. Дело вне допуска/роли неотличимо от несуществующего (ТБ-020/021).
/// </summary>
public sealed record ListCaseAuthorizationsQuery(int CaseId) : IRequest<ResponseDto<IReadOnlyList<CaseAuthorizationItem>>>
{
    /// <inheritdoc cref="ListCaseAuthorizationsQuery" />
    public sealed class Handler(IAccessContextProvider accessProvider, ICaseScope caseScope)
        : IRequestHandler<ListCaseAuthorizationsQuery, ResponseDto<IReadOnlyList<CaseAuthorizationItem>>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<IReadOnlyList<CaseAuthorizationItem>>> Handle(
            ListCaseAuthorizationsQuery query, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(query);

            // Fail-closed (ТБ-020/021): сначала доступность дела, затем — его основания.
            var access = await accessProvider.GetCurrentAsync(cancellationToken);
            var caseItem = await caseScope.GetCaseAsync(query.CaseId, access, cancellationToken);
            if (caseItem is null)
            {
                return ResponseDto<IReadOnlyList<CaseAuthorizationItem>>.NotFound("Дело не найдено или недоступно.");
            }

            var authorizations = await caseScope.ListAuthorizationsAsync(caseItem.CaseId, access, cancellationToken);
            return ResponseDto<IReadOnlyList<CaseAuthorizationItem>>.Ok(authorizations, authorizations.Count);
        }
    }
}
