using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.DocFlow.Domain.Services;
using Mediator;

namespace ISC.AI.Modules.DocFlow.Application.Features.Directory;

/// <summary>Кандидаты в исполнители по подразделению назначения (§4.1).</summary>
public sealed record ListAssigneeCandidatesQuery(int DivisionId)
    : IRequest<ResponseDto<IReadOnlyList<UserItem>>>
{
    /// <inheritdoc cref="ListAssigneeCandidatesQuery" />
    public sealed class Handler(IAssignmentCandidateDirectory candidates)
        : IRequestHandler<ListAssigneeCandidatesQuery, ResponseDto<IReadOnlyList<UserItem>>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<IReadOnlyList<UserItem>>> Handle(
            ListAssigneeCandidatesQuery query, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(query);

            if (query.DivisionId <= 0)
            {
                // Подразделение ещё не выбрано — предлагать некого. Пустой список честнее, чем
                // «все подряд»: иначе человек выберет исполнителя, а потом сменит подразделение,
                // и выбор молча станет недопустимым.
                return ResponseDto<IReadOnlyList<UserItem>>.Ok([], 0);
            }

            var items = await candidates.ListAssigneesAsync(query.DivisionId, cancellationToken);
            return ResponseDto<IReadOnlyList<UserItem>>.Ok(items, items.Count);
        }
    }
}
