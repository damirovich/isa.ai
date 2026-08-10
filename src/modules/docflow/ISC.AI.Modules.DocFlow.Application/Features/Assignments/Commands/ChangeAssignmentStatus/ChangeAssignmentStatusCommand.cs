using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.DocFlow.Application.Features.Notifications;
using ISC.AI.Modules.DocFlow.Domain.Enums;
using ISC.AI.Modules.DocFlow.Domain.Services;
using Mediator;

namespace ISC.AI.Modules.DocFlow.Application.Features.Assignments;

/// <summary>
/// Сменить статус назначения (§4.2): матрица §4.5, «Просрочено» — только система, вход/выход
/// «Контроля» фиксирует/сбрасывает контролёра, переход пишется в историю, агрегат пересчитывается.
/// </summary>
public sealed record ChangeAssignmentStatusCommand(
    int AssignmentId, AssignmentStatus NewStatus, string? Comment,
    IReadOnlyList<UploadedFile>? Files = null)
    : IRequest<ResponseDto<bool>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.Modify;

    /// <inheritdoc />
    public string? AuditSummary => $"docflow:assignment:{AssignmentId}:status:{NewStatus}";

    /// <inheritdoc cref="ChangeAssignmentStatusCommand" />
    public sealed class Handler(
        IDocumentStore store, IAccessContextProvider accessProvider, DocFlowEventNotifier notifier)
        : IRequestHandler<ChangeAssignmentStatusCommand, ResponseDto<bool>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<bool>> Handle(
            ChangeAssignmentStatusCommand command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);
            var access = await accessProvider.GetCurrentAsync(cancellationToken);

            var result = await store.ChangeAssignmentStatusAsync(
                command.AssignmentId, command.NewStatus, command.Comment,
                access, command.Files, cancellationToken);

            if (result == DocumentWriteStatus.Ok)
            {
                // Участники читаются ПОСЛЕ записи: вход в «Контроль» назначает контролёра, и он тоже
                // должен получить уведомление о собственном назначении контролёром (разд. 5).
                var participants = await store.GetAssignmentParticipantsAsync(
                    command.AssignmentId, access, cancellationToken);
                if (participants is not null)
                {
                    await DocFlowEventNotifier.SafeAsync(() => notifier.AssignmentStatusChangedAsync(
                        participants, command.NewStatus, access.NumericSubjectId, cancellationToken));
                }
            }

            return result switch
            {
                DocumentWriteStatus.Ok => ResponseDto<bool>.Ok(true),
                DocumentWriteStatus.NotFound => ResponseDto<bool>.NotFound("Назначение не найдено."),
                DocumentWriteStatus.OverdueIsAutomatic =>
                    ResponseDto<bool>.BadRequest("«Просрочено» устанавливается системой автоматически (ТЗ §4.2)."),
                DocumentWriteStatus.InvalidTransition =>
                    ResponseDto<bool>.BadRequest("Такой переход статуса не допускается (ТЗ §4.5)."),
                DocumentWriteStatus.Conflict =>
                    ResponseDto<bool>.BadRequest("Назначение изменено параллельно — обновите данные и повторите."),
                _ => ResponseDto<bool>.Fail("Не удалось изменить статус назначения."),
            };
        }
    }
}
