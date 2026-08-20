using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Inspector.Domain.Services;
using Mediator;

namespace ISC.AI.Profile.Inspector.Application.Features.Norms;

/// <summary>
/// Карточка нормы: реквизиты, редакции, привязанные документы корпуса (ТФ-НПА-02).
/// Реквизиты документов — в пределах решётки допуска субъекта (ТБ-020/021): вне допуска
/// связка показывается как «документ №N» без заголовка и грифа.
/// </summary>
public sealed record GetNormQuery(int NormId) : IRequest<ResponseDto<NormDetails>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.View;

    /// <inheritdoc />
    public string? AuditSummary => $"inspector:norm:{NormId}:view";

    /// <inheritdoc cref="GetNormQuery" />
    public sealed class Handler(INormRegistryStore store, IAccessContextProvider accessProvider)
        : IRequestHandler<GetNormQuery, ResponseDto<NormDetails>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<NormDetails>> Handle(GetNormQuery query, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(query);
            var access = await accessProvider.GetCurrentAsync(cancellationToken);
            var details = await store.GetAsync(query.NormId, access, cancellationToken);
            return details is null
                ? ResponseDto<NormDetails>.NotFound("Норма не найдена.")
                : ResponseDto<NormDetails>.Ok(details);
        }
    }
}
