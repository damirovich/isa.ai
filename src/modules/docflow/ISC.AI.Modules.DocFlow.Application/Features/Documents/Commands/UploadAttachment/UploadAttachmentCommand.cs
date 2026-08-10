using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.DocFlow.Application.Features.Notifications;
using ISC.AI.Modules.DocFlow.Domain.Enums;
using ISC.AI.Modules.DocFlow.Domain.Services;
using Mediator;

namespace ISC.AI.Modules.DocFlow.Application.Features.Documents;

/// <summary>Прикрепить сопутствующий файл к документу.</summary>
public sealed record UploadAttachmentCommand(int DocumentId, string FileName, string ContentType, byte[] Content)
    : IRequest<ResponseDto<bool>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.Modify;

    /// <inheritdoc />
    public string? AuditSummary => $"docflow:document:{DocumentId}:attachment:{FileName}";

    /// <inheritdoc cref="UploadAttachmentCommand" />
    public sealed class Handler(IDocumentStore store, IAccessContextProvider accessProvider)
        : IRequestHandler<UploadAttachmentCommand, ResponseDto<bool>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<bool>> Handle(
            UploadAttachmentCommand command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);
            var access = await accessProvider.GetCurrentAsync(cancellationToken);
            var result = await store.AddAttachmentAsync(
                command.DocumentId,
                new UploadedFile(command.FileName, command.ContentType, command.Content),
                access, cancellationToken);
            return result switch
            {
                DocumentWriteStatus.Ok => ResponseDto<bool>.Ok(true),
                DocumentWriteStatus.TooManyAttachments =>
                    ResponseDto<bool>.BadRequest("У документа уже 10 сопутствующих вложений — больше нельзя (ТЗ §3.3.1)."),
                _ => ResponseDto<bool>.NotFound("Документ не найден."),
            };
        }
    }
}
