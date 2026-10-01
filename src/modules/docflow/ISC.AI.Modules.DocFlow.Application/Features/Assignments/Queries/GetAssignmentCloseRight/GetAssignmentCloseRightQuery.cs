using ISC.AI.Abstractions.Application;
using ISC.AI.Modules.DocFlow.Domain.Services;
using Mediator;

namespace ISC.AI.Modules.DocFlow.Application.Features.Assignments;

/// <summary>
/// Может ли текущий пользователь снимать назначения с контроля (§4.2) — чтобы меню показало пункт неактивным
/// с объяснением, а не отказало после нажатия. Решает всё равно сервер (<see cref="ChangeAssignmentStatusCommand"/>).
/// </summary>
/// <remarks>Не аудируется: ответ — только «да/нет» о собственной роли, данных документа в нём нет.</remarks>
public sealed record GetAssignmentCloseRightQuery : IRequest<ResponseDto<bool>>
{
    /// <inheritdoc cref="GetAssignmentCloseRightQuery" />
    public sealed class Handler(IDocFlowAdministration administration)
        : IRequestHandler<GetAssignmentCloseRightQuery, ResponseDto<bool>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<bool>> Handle(
            GetAssignmentCloseRightQuery query, CancellationToken cancellationToken) =>
            ResponseDto<bool>.Ok(await administration.CanCloseAssignmentsAsync(cancellationToken));
    }
}
