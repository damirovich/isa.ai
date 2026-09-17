using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Investigation.Domain.Services;
using Mediator;

namespace ISC.AI.Profile.Investigation.Application.Features.Persons;

/// <summary>
/// Подтверждённые появления фигуранта (ТФ-ПЕР-02): только записи, прошедшие два независимых «подтверждён»
/// (ТБ-073), каждая — со статусом «следственная версия» (ТЭ-005..007: не «совпадение», не «идентифицирован»).
/// </summary>
public sealed record ListAppearancesQuery(int PersonId)
    : IRequest<ResponseDto<IReadOnlyList<AppearanceRow>>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.View;

    /// <inheritdoc />
    public string? AuditSummary => $"investigation:person:{PersonId}:appearances:list";

    /// <inheritdoc cref="ListAppearancesQuery" />
    public sealed class Handler(IPersonStore persons, IAccessContextProvider accessProvider)
        : IRequestHandler<ListAppearancesQuery, ResponseDto<IReadOnlyList<AppearanceRow>>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<IReadOnlyList<AppearanceRow>>> Handle(
            ListAppearancesQuery query, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(query);

            var access = await accessProvider.GetCurrentAsync(cancellationToken);
            var rows = await persons.ListAppearancesAsync(query.PersonId, access, cancellationToken);
            return ResponseDto<IReadOnlyList<AppearanceRow>>.Ok(rows, rows.Count);
        }
    }
}
