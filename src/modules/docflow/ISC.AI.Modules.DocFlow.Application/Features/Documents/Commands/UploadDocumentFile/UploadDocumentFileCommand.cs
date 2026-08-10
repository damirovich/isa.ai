using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.BackgroundTasks;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.DocFlow.Application.Features.Notifications;
using ISC.AI.Modules.DocFlow.Domain.Enums;
using ISC.AI.Modules.DocFlow.Domain.Services;
using Mediator;
using Microsoft.Extensions.DependencyInjection;

namespace ISC.AI.Modules.DocFlow.Application.Features.Documents;

/// <summary>Загрузить версионируемый файл документа (§3.3): замена создаёт новую версию.</summary>
public sealed record UploadDocumentFileCommand(
    int DocumentId, string FileName, string ContentType, byte[] Content, DocumentLanguage Language)
    : IRequest<ResponseDto<bool>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.Modify;

    /// <inheritdoc />
    public string? AuditSummary => $"docflow:document:{DocumentId}:file:{FileName}";

    /// <inheritdoc cref="UploadDocumentFileCommand" />
    public sealed class Handler(IDocumentStore store, IAccessContextProvider accessProvider)
        : IRequestHandler<UploadDocumentFileCommand, ResponseDto<bool>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<bool>> Handle(
            UploadDocumentFileCommand command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);
            var access = await accessProvider.GetCurrentAsync(cancellationToken);
            var result = await store.AddDocumentFileAsync(
                command.DocumentId,
                new UploadedFile(command.FileName, command.ContentType, command.Content),
                command.Language, access, cancellationToken);
            return result == DocumentWriteStatus.Ok
                ? ResponseDto<bool>.Ok(true)
                : ResponseDto<bool>.NotFound("Документ не найден.");
        }
    }
}
