using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Investigation.Domain.Services;
using Mediator;

namespace ISC.AI.Profile.Investigation.Application.Features.Persons;

/// <summary>Эталоны фигуранта, включая заменённые (ТБ-077: прежний эталон сохраняется).</summary>
public sealed record ListReferencePhotosQuery(int PersonId)
    : IRequest<ResponseDto<IReadOnlyList<ReferencePhotoRow>>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.View;

    /// <inheritdoc />
    public string? AuditSummary => $"investigation:person:{PersonId}:reference-photos:list";

    /// <inheritdoc cref="ListReferencePhotosQuery" />
    public sealed class Handler(IPersonStore persons, IAccessContextProvider accessProvider)
        : IRequestHandler<ListReferencePhotosQuery, ResponseDto<IReadOnlyList<ReferencePhotoRow>>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<IReadOnlyList<ReferencePhotoRow>>> Handle(
            ListReferencePhotosQuery query, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(query);

            var access = await accessProvider.GetCurrentAsync(cancellationToken);
            var rows = await persons.ListReferencePhotosAsync(query.PersonId, access, cancellationToken);
            return ResponseDto<IReadOnlyList<ReferencePhotoRow>>.Ok(rows, rows.Count);
        }
    }
}
