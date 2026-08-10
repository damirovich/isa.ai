using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.DocFlow.Application.Features.Notifications;
using ISC.AI.Modules.DocFlow.Domain.Enums;
using ISC.AI.Modules.DocFlow.Domain.Services;
using Mediator;

namespace ISC.AI.Modules.DocFlow.Application.Features.Documents;

/// <summary>Удалить сопутствующее вложение документа (§3.3.1).</summary>
public sealed record DeleteAttachmentCommand(int AttachmentId)
    : IRequest<ResponseDto<bool>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.Modify;

    /// <inheritdoc />
    public string? AuditSummary => $"docflow:attachment:{AttachmentId}:delete";

    /// <inheritdoc cref="DeleteAttachmentCommand" />
    public sealed class Handler(IDocumentStore store, IAccessContextProvider accessProvider)
        : IRequestHandler<DeleteAttachmentCommand, ResponseDto<bool>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<bool>> Handle(
            DeleteAttachmentCommand command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);

            var access = await accessProvider.GetCurrentAsync(cancellationToken);
            var status = await store.DeleteAttachmentAsync(command.AttachmentId, access, cancellationToken);

            return status switch
            {
                DocumentWriteStatus.Ok => ResponseDto<bool>.Ok(true, "Вложение удалено."),
                _ => ResponseDto<bool>.NotFound("Вложение не найдено."),
            };
        }
    }
}
