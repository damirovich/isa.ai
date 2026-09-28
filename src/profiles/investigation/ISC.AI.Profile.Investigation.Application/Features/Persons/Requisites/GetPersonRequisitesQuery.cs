using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Investigation.Domain.Services;
using Mediator;

namespace ISC.AI.Profile.Investigation.Application.Features.Persons;

/// <summary>Адреса и автотранспорт фигуранта (ТФ-ПЕР-06). Решётка — в хранилище: недоступный фигурант — «не найден».</summary>
/// <param name="PersonId">Фигурант.</param>
public sealed record GetPersonRequisitesQuery(int PersonId) : IRequest<ResponseDto<PersonRequisites>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.View;

    /// <inheritdoc />
    public string? AuditSummary => $"investigation:person:{PersonId}:requisites:view";

    /// <inheritdoc cref="GetPersonRequisitesQuery" />
    public sealed class Handler(IPersonRequisiteStore store, IAccessContextProvider accessProvider)
        : IRequestHandler<GetPersonRequisitesQuery, ResponseDto<PersonRequisites>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<PersonRequisites>> Handle(GetPersonRequisitesQuery query, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(query);

            // Fail-closed (ТБ-021): без контекста допуска ничего не выдаётся; «нет» и «недоступен» — один ответ.
            var access = await accessProvider.GetCurrentAsync(cancellationToken);
            var requisites = await store.GetAsync(query.PersonId, access, cancellationToken);
            return requisites is null
                ? ResponseDto<PersonRequisites>.NotFound(PersonGuard.NotFound)
                : ResponseDto<PersonRequisites>.Ok(requisites);
        }
    }
}
