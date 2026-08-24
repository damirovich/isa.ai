using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Inspector.Domain.Services;
using Mediator;

namespace ISC.AI.Profile.Inspector.Application.Features.Methods;

/// <summary>Карточка методики. Недоступная по допуску неотличима от несуществующей (ТБ-020-стиль).</summary>
public sealed record GetMethodDocumentQuery(int MethodId)
    : IRequest<ResponseDto<MethodDocumentDetails>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.View;

    /// <inheritdoc />
    public string? AuditSummary => $"inspector:method:{MethodId}:view";

    /// <inheritdoc cref="GetMethodDocumentQuery" />
    public sealed class Handler(IMethodRegistryStore store, IAccessContextProvider accessContextProvider)
        : IRequestHandler<GetMethodDocumentQuery, ResponseDto<MethodDocumentDetails>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<MethodDocumentDetails>> Handle(
            GetMethodDocumentQuery query, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(query);

            var access = await accessContextProvider.GetCurrentAsync(cancellationToken);
            var details = await store.GetAsync(query.MethodId, access.MaxClassification, cancellationToken);
            return details is null
                ? ResponseDto<MethodDocumentDetails>.NotFound("Методика не найдена.")
                : ResponseDto<MethodDocumentDetails>.Ok(details);
        }
    }
}
