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

/// <summary>Карточка носителя (ТФ-МЕД-03): метаданные, лица (рамки/шкала/вырезки) и дело, к которому привязан.</summary>
/// <param name="Asset">Носитель.</param>
/// <param name="Faces">Лица носителя по кадру и позиции.</param>
/// <param name="CaseId">Дело, к которому носитель привязан; <see langword="null"/> — не привязан.</param>
public sealed record MediaAssetDetails(MediaAssetRow Asset, IReadOnlyList<FaceRow> Faces, int? CaseId);

/// <summary>Карточка носителя по идентификатору (ТФ-МЕД-03). Просмотр биометрического материала аудируется (ТБ-030).</summary>
public sealed record GetMediaAssetQuery(int AssetId) : IRequest<ResponseDto<MediaAssetDetails>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.View;

    /// <inheritdoc />
    public string? AuditSummary => $"media:asset:{AssetId}:view";

    /// <inheritdoc cref="GetMediaAssetQuery" />
    public sealed class Handler(IAccessContextProvider accessProvider, IMediaCatalog catalog, ICaseScope caseScope)
        : IRequestHandler<GetMediaAssetQuery, ResponseDto<MediaAssetDetails>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<MediaAssetDetails>> Handle(
            GetMediaAssetQuery query, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(query);

            // Fail-closed (ТБ-020/021): решётка применяется каталогом на стороне БД.
            var access = await accessProvider.GetCurrentAsync(cancellationToken);
            var asset = await catalog.GetAsync(query.AssetId, access, cancellationToken);
            if (asset is null)
            {
                return ResponseDto<MediaAssetDetails>.NotFound("Носитель не найден или недоступен.");
            }

            var faces = await catalog.ListFacesAsync(asset.Id, access, cancellationToken);
            var caseId = await caseScope.GetCaseIdForAssetAsync(asset.Id, cancellationToken);
            return ResponseDto<MediaAssetDetails>.Ok(new MediaAssetDetails(asset, faces, caseId));
        }
    }
}
