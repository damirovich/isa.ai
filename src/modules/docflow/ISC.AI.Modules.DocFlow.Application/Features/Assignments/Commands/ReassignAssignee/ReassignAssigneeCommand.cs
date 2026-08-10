using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.DocFlow.Application.Features.Notifications;
using ISC.AI.Modules.DocFlow.Domain.Enums;
using ISC.AI.Modules.DocFlow.Domain.Services;
using Mediator;

namespace ISC.AI.Modules.DocFlow.Application.Features.Assignments;

/// <summary>Переназначить исполнителя (§4.7): срок и статус сохраняются, меняется только ответственный.</summary>
public sealed record ReassignAssigneeCommand(int AssignmentId, int NewAssigneeUserId, string? Reason)
    : IRequest<ResponseDto<bool>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.Modify;

    /// <inheritdoc />
    public string? AuditSummary => $"docflow:assignment:{AssignmentId}:reassign:{NewAssigneeUserId}";

    /// <inheritdoc cref="ReassignAssigneeCommand" />
    public sealed class Handler(
        IDocumentStore store, IAccessContextProvider accessProvider, DocFlowEventNotifier notifier)
        : IRequestHandler<ReassignAssigneeCommand, ResponseDto<bool>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<bool>> Handle(
            ReassignAssigneeCommand command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);

            var access = await accessProvider.GetCurrentAsync(cancellationToken);
            var result = await store.ReassignAssigneeAsync(
                command.AssignmentId, command.NewAssigneeUserId, command.Reason, access, cancellationToken);

            // Notice отсутствует при успехе = «назначили того же» — уведомлять не о чем.
            if (result is { Status: DocumentWriteStatus.Ok, Notice: { } notice })
            {
                await DocFlowEventNotifier.SafeAsync(() => notifier.AssigneeReassignedAsync(
                    notice, access.NumericSubjectId, cancellationToken));
            }

            return result.Status switch
            {
                DocumentWriteStatus.Ok => ResponseDto<bool>.Ok(true),
                DocumentWriteStatus.NotFound => ResponseDto<bool>.NotFound("Назначение не найдено."),
                DocumentWriteStatus.InvalidTransition =>
                    ResponseDto<bool>.BadRequest(
                        "Исполненное и снятое с контроля назначение не переназначается — это переписывание "
                        + "уже состоявшегося факта. Верните назначение в работу, если исполнение продолжается."),
                DocumentWriteStatus.AssigneeOutsideDivision =>
                    ResponseDto<bool>.BadRequest(
                        "Исполнителю не разрешено подразделение назначения — он не увидел бы этот документ."),
                DocumentWriteStatus.Conflict =>
                    ResponseDto<bool>.BadRequest("Назначение изменено параллельно — обновите данные и повторите."),
                _ => ResponseDto<bool>.Fail("Не удалось переназначить исполнителя."),
            };
        }
    }
}
