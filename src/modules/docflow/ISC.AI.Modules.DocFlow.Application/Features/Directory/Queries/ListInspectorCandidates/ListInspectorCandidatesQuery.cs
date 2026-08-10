using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.DocFlow.Domain.Services;
using Mediator;

namespace ISC.AI.Modules.DocFlow.Application.Features.Directory;

/// <summary>
/// Кандидаты в ответственные инспекторы документа (§3.2).
/// </summary>
/// <remarks>
/// Отдельно от <see cref="ListUsersQuery"/>: там ВЕСЬ активный реестр — он нужен, например, чтобы
/// подставить упоминание в комментарий или показать имя. Здесь — только те, кому инспекторство
/// вообще положено; предлагать остальных значит приглашать к ошибке, которая вылезет после
/// регистрации.
/// </remarks>
public sealed record ListInspectorCandidatesQuery : IRequest<ResponseDto<IReadOnlyList<UserItem>>>
{
    /// <inheritdoc cref="ListInspectorCandidatesQuery" />
    public sealed class Handler(IAssignmentCandidateDirectory candidates)
        : IRequestHandler<ListInspectorCandidatesQuery, ResponseDto<IReadOnlyList<UserItem>>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<IReadOnlyList<UserItem>>> Handle(
            ListInspectorCandidatesQuery query, CancellationToken cancellationToken)
        {
            var items = await candidates.ListInspectorsAsync(cancellationToken);
            return ResponseDto<IReadOnlyList<UserItem>>.Ok(items, items.Count);
        }
    }
}
