using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Investigation.Domain.Services;
using Mediator;

namespace ISC.AI.Profile.Investigation.Application.Features.Persons;

/// <summary>Карточка фигуранта (ТФ-ПЕР-01).</summary>
public sealed record GetPersonQuery(int PersonId) : IRequest<ResponseDto<PersonRow>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.View;

    /// <inheritdoc />
    public string? AuditSummary => $"investigation:person:{PersonId}:view";

    /// <inheritdoc cref="GetPersonQuery" />
    public sealed class Handler(IPersonStore persons, IAccessContextProvider accessProvider)
        : IRequestHandler<GetPersonQuery, ResponseDto<PersonRow>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<PersonRow>> Handle(GetPersonQuery query, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(query);

            var access = await accessProvider.GetCurrentAsync(cancellationToken);
            var row = await persons.GetAsync(query.PersonId, access, cancellationToken);

            // Неразличимость (ТБ-020/021): «нет» и «недоступен» — один ответ.
            return row is null
                ? ResponseDto<PersonRow>.NotFound(PersonGuard.NotFound)
                : ResponseDto<PersonRow>.Ok(row);
        }
    }
}
