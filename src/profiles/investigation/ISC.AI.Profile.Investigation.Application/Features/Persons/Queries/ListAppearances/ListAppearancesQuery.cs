using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.Media.Domain.Services;
using ISC.AI.Profile.Investigation.Domain.Services;
using Mediator;

namespace ISC.AI.Profile.Investigation.Application.Features.Persons;

/// <summary>
/// Подтверждённые появления фигуранта (ТФ-ПЕР-02): только записи, прошедшие два независимых «подтверждён»
/// (ТБ-073), каждая — со статусом «следственная версия» (ТЭ-005..007: не «совпадение», не «идентифицирован»).
/// У появления в видео — отрезок «с … по …»: первый и последний кадр трека лица (ADR-0037).
/// </summary>
public sealed record ListAppearancesQuery(int PersonId)
    : IRequest<ResponseDto<IReadOnlyList<AppearanceRow>>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.View;

    /// <inheritdoc />
    public string? AuditSummary => $"investigation:person:{PersonId}:appearances:list";

    /// <inheritdoc cref="ListAppearancesQuery" />
    public sealed class Handler(IPersonStore persons, IAccessContextProvider accessProvider, IMediaCatalog catalog)
        : IRequestHandler<ListAppearancesQuery, ResponseDto<IReadOnlyList<AppearanceRow>>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<IReadOnlyList<AppearanceRow>>> Handle(
            ListAppearancesQuery query, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(query);

            var access = await accessProvider.GetCurrentAsync(cancellationToken);
            var rows = await persons.ListAppearancesAsync(query.PersonId, access, cancellationToken);

            // Отрезки треков — только у появлений в видео; кадры трека пакет «Медиа» читает под той же решёткой.
            var videoRows = rows.Where(r => r.FrameTimestampMs is not null).ToList();
            if (videoRows.Count == 0)
            {
                return ResponseDto<IReadOnlyList<AppearanceRow>>.Ok(rows, rows.Count);
            }

            var spans = await catalog.GetTrackSpansAsync(
                videoRows.Select(r => new FaceTrackRequest(r.MediaFaceId, r.MediaAssetId, r.FrameTimestampMs)).ToList(),
                access,
                cancellationToken);
            var result = rows
                .Select(r => spans.TryGetValue(r.MediaFaceId, out var span)
                    ? r with { TrackStartMs = span.StartMs, TrackEndMs = span.EndMs, TrackFrames = span.Frames }
                    : r)
                .ToList();
            return ResponseDto<IReadOnlyList<AppearanceRow>>.Ok(result, result.Count);
        }
    }
}
