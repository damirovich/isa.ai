using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Profile.Inspector.Domain.Services;
using Mediator;

namespace ISC.AI.Profile.Inspector.Application.Features.Violations;

/// <summary>Карточка нарушения — для диалога правки.</summary>
public sealed record GetViolationQuery(int ViolationId) : IRequest<ResponseDto<ViolationDetails>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.View;

    /// <inheritdoc />
    public string? AuditSummary => $"inspector:violation:{ViolationId}:view";

    /// <inheritdoc cref="GetViolationQuery" />
    public sealed class Handler(IViolationStore store) : IRequestHandler<GetViolationQuery, ResponseDto<ViolationDetails>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<ViolationDetails>> Handle(
            GetViolationQuery query, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(query);
            var details = await store.GetAsync(query.ViolationId, cancellationToken);
            return details is null
                ? ResponseDto<ViolationDetails>.NotFound("Нарушение не найдено.")
                : ResponseDto<ViolationDetails>.Ok(details);
        }
    }
}
