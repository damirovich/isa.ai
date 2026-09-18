using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Investigation.Domain.Services;
using Mediator;

namespace ISC.AI.Profile.Investigation.Application.Features.Cases;

/// <summary>Карточка дела (ТФ-ДЕЛ-02): реквизиты, привязанные носители, основания поиска.</summary>
public sealed record GetCaseQuery(int CaseId) : IRequest<ResponseDto<CaseDetails>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.View;

    /// <inheritdoc />
    public string? AuditSummary => $"investigation:case:{CaseId}:view";

    /// <inheritdoc cref="GetCaseQuery" />
    public sealed class Handler(ICaseStore cases, IAccessContextProvider accessProvider)
        : IRequestHandler<GetCaseQuery, ResponseDto<CaseDetails>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<CaseDetails>> Handle(GetCaseQuery query, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(query);

            var access = await accessProvider.GetCurrentAsync(cancellationToken);
            var details = await cases.GetAsync(query.CaseId, access, cancellationToken);

            // Неразличимость (ТБ-020/021): «нет такого дела» и «дело вне допуска» — один и тот же ответ.
            return details is null
                ? ResponseDto<CaseDetails>.NotFound("Дело не найдено или недоступно.")
                : ResponseDto<CaseDetails>.Ok(details);
        }
    }
}
