using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.Media.Domain.Model;
using ISC.AI.Modules.Media.Domain.Services;
using Mediator;

namespace ISC.AI.Modules.Media.Application.Features.Assets;

/// <summary>Карточка носителя (ТФ-МЕД-03): метаданные, лица (рамки/шкала/вырезки) и дело, к которому привязан.</summary>
/// <param name="Asset">Носитель.</param>
/// <param name="Faces">Лица носителя по кадру и позиции.</param>
/// <param name="CaseId">
/// Дело носителя, ДОСТУПНОЕ субъекту; <see langword="null"/> — не привязан либо все его дела недоступны
/// (неразличимо, ТБ-020/021 — чужое дело по идентификатору не раскрывается).
/// </param>
/// <param name="PersonSpans">
/// Подтверждённые фигуранты на видео — отрезками треков их лиц (отметки на ленте, ADR-0038); у фото и аудио —
/// пусто. Только фигуранты и появления, доступные субъекту (порт профиля <see cref="ICaseScope.ListAssetAppearancesAsync"/>).
/// </param>
public sealed record MediaAssetDetails(
    MediaAssetRow Asset, IReadOnlyList<FaceRow> Faces, int? CaseId, IReadOnlyList<AssetPersonSpan>? PersonSpans = null);

/// <summary>
/// Отрезок видео, на котором подтверждён фигурант (ADR-0038): трек лица из подтверждённого появления, а если трека нет
/// (видео проиндексировано до треков, лицо пересоздано переиндексацией) — момент кадра появления.
/// </summary>
/// <param name="PersonId">Фигурант.</param>
/// <param name="DisplayName">Подпись фигуранта.</param>
/// <param name="StartMs">Начало отрезка, мс.</param>
/// <param name="EndMs">Конец отрезка, мс (равен началу — одна точка).</param>
public sealed record AssetPersonSpan(int PersonId, string DisplayName, long StartMs, long EndMs);

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

            // Сужение по делам субъекта ПОВЕРХ решётки (ТБ-071, ТФ-ДЕЛ-03): следователь того же подразделения
            // и того же допуска перебором id не должен читать носители чужих дел, а субъект без роли — ничего.
            // Отказ неотличим от «не найден» (ТБ-020/021).
            if (!await caseScope.IsAssetAccessibleAsync(asset.Id, access, cancellationToken))
            {
                return ResponseDto<MediaAssetDetails>.NotFound("Носитель не найден или недоступен.");
            }

            var faces = await catalog.ListFacesAsync(asset.Id, access, cancellationToken);
            var caseId = await caseScope.GetCaseIdForAssetAsync(asset.Id, access, cancellationToken);
            var personSpans = asset.Kind == MediaKind.Video
                ? await ReadPersonSpansAsync(asset.Id, access, cancellationToken)
                : [];
            return ResponseDto<MediaAssetDetails>.Ok(new MediaAssetDetails(asset, faces, caseId, personSpans));
        }

        /// <summary>
        /// Подтверждённые фигуранты видео отрезками (ADR-0038): появления — от профиля (под его решёткой и ролью),
        /// отрезки — по трекам лиц (под решёткой пакета). Появление, у лица которого трека нет, — точкой на кадре.
        /// </summary>
        private async Task<IReadOnlyList<AssetPersonSpan>> ReadPersonSpansAsync(
            int assetId, AccessContext access, CancellationToken cancellationToken)
        {
            var appearances = await caseScope.ListAssetAppearancesAsync(assetId, access, cancellationToken);
            if (appearances.Count == 0)
            {
                return [];
            }

            var requests = appearances
                .Select(a => new FaceTrackRequest(a.FaceId, assetId, a.FrameTimestampMs))
                .Distinct()
                .ToList();
            var tracks = await catalog.GetTrackSpansAsync(requests, access, cancellationToken);

            var spans = new List<AssetPersonSpan>(appearances.Count);
            foreach (var appearance in appearances)
            {
                if (tracks.TryGetValue(appearance.FaceId, out var track))
                {
                    spans.Add(new AssetPersonSpan(appearance.PersonId, appearance.DisplayName, track.StartMs, track.EndMs));
                }
                else if (appearance.FrameTimestampMs is { } moment)
                {
                    spans.Add(new AssetPersonSpan(appearance.PersonId, appearance.DisplayName, moment, moment));
                }
            }

            // Несколько появлений одного фигуранта на одном треке (подтверждены по разным кадрам) — одна отметка.
            return spans.Distinct().OrderBy(s => s.StartMs).ThenBy(s => s.PersonId).ToList();
        }
    }
}
