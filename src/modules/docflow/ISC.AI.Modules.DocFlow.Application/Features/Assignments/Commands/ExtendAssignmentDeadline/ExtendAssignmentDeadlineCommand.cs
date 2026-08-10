using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.DocFlow.Application.Features.Notifications;
using ISC.AI.Modules.DocFlow.Domain.Enums;
using ISC.AI.Modules.DocFlow.Domain.Services;
using Mediator;

namespace ISC.AI.Modules.DocFlow.Application.Features.Assignments;

/// <summary>Продлить срок назначения (§4.6): основание обязательно, возврат «В работу» автоматический.</summary>
public sealed record ExtendAssignmentDeadlineCommand(
    int AssignmentId, DateOnly NewDeadline, string Reason,
    IReadOnlyList<UploadedFile>? Files = null)
    : IRequest<ResponseDto<bool>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.Modify;

    /// <inheritdoc />
    public string? AuditSummary => $"docflow:assignment:{AssignmentId}:extend:{NewDeadline:yyyy-MM-dd}";

    /// <inheritdoc cref="ExtendAssignmentDeadlineCommand" />
    public sealed class Handler(
        IDocumentStore store, IAccessContextProvider accessProvider, DocFlowEventNotifier notifier)
        : IRequestHandler<ExtendAssignmentDeadlineCommand, ResponseDto<bool>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<bool>> Handle(
            ExtendAssignmentDeadlineCommand command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);
            var access = await accessProvider.GetCurrentAsync(cancellationToken);

            // §4.6: инициатор продления фиксируется обязательно — без числового субъекта не продлеваем.
            if (access.NumericSubjectId is null)
            {
                return ResponseDto<bool>.BadRequest("Продление срока требует аутентифицированного пользователя.");
            }

            // Участники — ДО записи: в тексте уведомления нужен СТАРЫЙ срок, после продления он утрачен.
            var participants = await store.GetAssignmentParticipantsAsync(
                command.AssignmentId, access, cancellationToken);

            var result = await store.ExtendDeadlineAsync(
                command.AssignmentId, command.NewDeadline, command.Reason, access,
                command.Files, cancellationToken);

            if (result == DocumentWriteStatus.Ok && participants is not null)
            {
                await DocFlowEventNotifier.SafeAsync(() => notifier.DeadlineExtendedAsync(
                    participants, command.NewDeadline, access.NumericSubjectId, cancellationToken));
            }

            return result switch
            {
                DocumentWriteStatus.Ok => ResponseDto<bool>.Ok(true),
                DocumentWriteStatus.NotFound => ResponseDto<bool>.NotFound("Назначение не найдено."),
                DocumentWriteStatus.NoDeadline =>
                    ResponseDto<bool>.BadRequest("У назначения нет срока — продлевать нечего."),
                DocumentWriteStatus.InvalidTransition =>
                    ResponseDto<bool>.BadRequest("Назначение снято с контроля — срок не продлевается (ТЗ §4.2)."),
                DocumentWriteStatus.Conflict =>
                    ResponseDto<bool>.BadRequest("Назначение изменено параллельно — обновите данные и повторите."),
                _ => ResponseDto<bool>.Fail("Не удалось продлить срок назначения."),
            };
        }
    }
}
