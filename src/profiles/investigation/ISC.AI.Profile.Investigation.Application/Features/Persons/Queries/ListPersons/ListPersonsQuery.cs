using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Investigation.Domain.Services;
using Mediator;

namespace ISC.AI.Profile.Investigation.Application.Features.Persons;

/// <summary>Фигуранты дела (ТФ-ПЕР-01). Решётка — в хранилище: недоступное дело даёт пустой список.</summary>
public sealed record ListPersonsQuery(int CaseId) : IRequest<ResponseDto<IReadOnlyList<PersonRow>>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.View;

    /// <inheritdoc />
    public string? AuditSummary => $"investigation:case:{CaseId}:persons:list";

    /// <inheritdoc cref="ListPersonsQuery" />
    public sealed class Handler(IPersonStore persons, IAccessContextProvider accessProvider)
        : IRequestHandler<ListPersonsQuery, ResponseDto<IReadOnlyList<PersonRow>>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<IReadOnlyList<PersonRow>>> Handle(
            ListPersonsQuery query, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(query);

            // Fail-closed (ТБ-021): без контекста допуска список не выдаётся.
            var access = await accessProvider.GetCurrentAsync(cancellationToken);
            var rows = await persons.ListByCaseAsync(query.CaseId, access, cancellationToken);
            return ResponseDto<IReadOnlyList<PersonRow>>.Ok(rows, rows.Count);
        }
    }
}
