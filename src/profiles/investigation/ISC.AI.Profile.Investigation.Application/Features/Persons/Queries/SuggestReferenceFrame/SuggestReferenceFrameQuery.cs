using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentValidation;
using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.Media.Domain.Model;
using ISC.AI.Modules.Media.Domain.Services;
using ISC.AI.Profile.Investigation.Domain.Services;
using Mediator;

namespace ISC.AI.Profile.Investigation.Application.Features.Persons;

/// <summary>Кадр-кандидат в эталон: носитель, лицо, вырезка и оценки кадра.</summary>
/// <param name="AssetId">Носитель.</param>
/// <param name="FaceId">Лицо.</param>
/// <param name="AssetFileName">Имя файла носителя.</param>
/// <param name="FrameTimestampMs">Момент кадра (видео).</param>
/// <param name="CropStoredFileName">Вырезка лица — для показа через эндпоинт с проверкой доступа.</param>
/// <param name="Metrics">Оценки кадра (ADR-0058); <see langword="null"/> — лицо проиндексировано до них.</param>
public sealed record ReferenceFrameCandidate(
    int AssetId, int FaceId, string AssetFileName, long? FrameTimestampMs, string? CropStoredFileName, FrameMetrics? Metrics);

/// <summary>
/// Подсказка эталона (ТФ-МЕД-21, ADR-0058 п. 4): лучший кадр из появлений фигуранта и лучший из его действующих
/// эталонов для сравнения.
/// </summary>
/// <param name="Suggested">Лучший кадр, ещё не эталон; <see langword="null"/> — кадров нет.</param>
/// <param name="Current">Лучший из действующих эталонов с лицом; <see langword="null"/> — эталонов нет.</param>
/// <param name="FramesConsidered">Сколько кадров сравнено.</param>
/// <param name="SuggestedIsBetter">Предложенный кадр лучше действующего эталона (или эталона нет, или его не с чем сравнить).</param>
public sealed record ReferenceFrameSuggestion(
    ReferenceFrameCandidate? Suggested, ReferenceFrameCandidate? Current, int FramesConsidered, bool SuggestedIsBetter);

/// <summary>
/// Подсказка лучшего кадра для эталона фигуранта (ТФ-МЕД-21, ADR-0058 п. 4): из подтверждённых и не отозванных
/// появлений фигуранта на материалах ЕГО дела — у видео среди всех кадров трека появления, у фото — само лицо; лица,
/// уже ставшие действующими эталонами, не предлагаются; кадры упорядочиваются правилом <see cref="FaceRanking"/>.
/// Эталоном кадр делает человек — командой «Новый эталон» (ТБ-077).
/// </summary>
/// <remarks>
/// ИНВАРИАНТ (ТБ-071, ТБ-077): источник подсказки — только носители дела фигуранта, доступные субъекту (то же правило,
/// что у <see cref="AddReferencePhotoCommand"/>): материал чужого дела (кандидат взаимного поиска) эталоном не
/// предлагается. Фигурант, носители и лица читаются под решёткой (ТБ-020/021). Журнал — номер фигуранта (ТБ-032).
/// </remarks>
/// <param name="PersonId">Фигурант.</param>
public sealed record SuggestReferenceFrameQuery(int PersonId) : IRequest<ResponseDto<ReferenceFrameSuggestion>>, IAuditableRequest
{
    /// <summary>Насколько оценка кадра должна превосходить действующий эталон, чтобы подсказка назвала его лучшим.</summary>
    public const float BetterMargin = 0.02f;

    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.View;

    /// <inheritdoc />
    public string? AuditSummary => $"investigation:person:{PersonId}:reference-frame:view";

    /// <inheritdoc cref="SuggestReferenceFrameQuery" />
    public sealed class Handler(IPersonStore persons, ICaseScope caseScope, IMediaCatalog catalog, IAccessContextProvider accessProvider)
        : IRequestHandler<SuggestReferenceFrameQuery, ResponseDto<ReferenceFrameSuggestion>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<ReferenceFrameSuggestion>> Handle(SuggestReferenceFrameQuery query, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(query);

            var access = await accessProvider.GetCurrentAsync(cancellationToken);
            if (await persons.GetAsync(query.PersonId, access, cancellationToken) is not { } person)
            {
                return ResponseDto<ReferenceFrameSuggestion>.NotFound(PersonGuard.NotFound);
            }

            var caseAssets = (await caseScope.GetAssetIdsAsync([person.CaseId], cancellationToken)).ToHashSet();
            var references = (await persons.ListReferencePhotosAsync(person.Id, access, cancellationToken))
                .Where(r => r.SupersededById is null && r.MediaFaceId is not null)
                .ToList();
            var referenceFaces = references.Select(r => r.MediaFaceId!.Value).ToHashSet();

            // Кадры появлений: только носители дела фигуранта, доступные субъекту; у видео — весь трек появления.
            var pool = new Dictionary<int, FaceRow>();
            var appearances = (await persons.ListAppearancesAsync(person.Id, access, cancellationToken))
                .Where(a => !a.IsRevoked && caseAssets.Contains(a.MediaAssetId));
            foreach (var group in appearances.GroupBy(a => a.MediaAssetId))
            {
                if (!await caseScope.IsAssetAccessibleAsync(group.Key, access, cancellationToken))
                {
                    continue;
                }

                var faces = await catalog.ListFacesAsync(group.Key, access, cancellationToken);
                foreach (var appearance in group)
                {
                    if (faces.FirstOrDefault(f => f.Id == appearance.MediaFaceId) is not { } face)
                    {
                        continue;
                    }

                    foreach (var frame in face.TrackId is { } track ? faces.Where(f => f.TrackId == track) : [face])
                    {
                        pool.TryAdd(frame.Id, frame);
                    }
                }
            }

            var suggested = FaceRanking.Best(pool.Values.Where(f => f.QualityAcceptable && !referenceFaces.Contains(f.Id)));

            // Действующие эталоны — для сравнения: лица читаются тем же каталогом под решёткой.
            var currentFaces = new List<FaceRow>();
            foreach (var faceId in referenceFaces)
            {
                if (await catalog.GetFaceAsync(faceId, access, cancellationToken) is { } face)
                {
                    currentFaces.Add(face);
                }
            }

            var current = FaceRanking.Best(currentFaces);
            var better = suggested is not null
                && (current is null || current.Frame is null || suggested.Frame is null
                    || suggested.Frame.Score > current.Frame.Score + BetterMargin);

            return ResponseDto<ReferenceFrameSuggestion>.Ok(new ReferenceFrameSuggestion(
                await CandidateAsync(suggested, access, cancellationToken),
                await CandidateAsync(current, access, cancellationToken),
                pool.Count,
                better));
        }

        private async Task<ReferenceFrameCandidate?> CandidateAsync(FaceRow? face, AccessContext access, CancellationToken cancellationToken)
        {
            if (face is null)
            {
                return null;
            }

            var asset = await catalog.GetAsync(face.AssetId, access, cancellationToken);
            return new ReferenceFrameCandidate(
                face.AssetId, face.Id, asset?.OriginalFileName ?? string.Empty, face.FrameTimestampMs, face.CropStoredFileName, face.Frame);
        }
    }
}

/// <summary>Правила запроса подсказки эталона.</summary>
public sealed class SuggestReferenceFrameValidator : AbstractValidator<SuggestReferenceFrameQuery>
{
    /// <inheritdoc cref="SuggestReferenceFrameValidator" />
    public SuggestReferenceFrameValidator()
    {
        RuleFor(q => q.PersonId).GreaterThan(0);
    }
}
