using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.Media.Domain.Services;
using Mediator;

namespace ISC.AI.Modules.Media.Application.Features.Assets;

/// <summary>Носители дела (ТФ-МЕД-03, ТФ-ДЕЛ-03): только доступные субъекту, новые первыми.</summary>
public sealed record ListCaseMediaQuery(int CaseId) : IRequest<ResponseDto<IReadOnlyList<MediaAssetRow>>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.View;

    /// <inheritdoc />
    public string? AuditSummary => $"media:case:{CaseId}:assets";

    /// <inheritdoc cref="ListCaseMediaQuery" />
    public sealed class Handler(IAccessContextProvider accessProvider, ICaseScope caseScope, IMediaCatalog catalog)
        : IRequestHandler<ListCaseMediaQuery, ResponseDto<IReadOnlyList<MediaAssetRow>>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<IReadOnlyList<MediaAssetRow>>> Handle(
            ListCaseMediaQuery query, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(query);

            // Fail-closed (ТБ-020/021, ТБ-071): дело вне допуска/роли неотличимо от несуществующего.
            var access = await accessProvider.GetCurrentAsync(cancellationToken);
            var caseItem = await caseScope.GetCaseAsync(query.CaseId, access, cancellationToken);
            if (caseItem is null)
            {
                return ResponseDto<IReadOnlyList<MediaAssetRow>>.NotFound("Дело не найдено или недоступно.");
            }

            var assetIds = await caseScope.GetAssetIdsAsync([caseItem.CaseId], cancellationToken);
            var assets = assetIds.Count == 0
                ? []
                : await catalog.ListAsync(assetIds, access, cancellationToken);

            return ResponseDto<IReadOnlyList<MediaAssetRow>>.Ok(assets, assets.Count);
        }
    }
}
