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
    public sealed class Handler(
        IDocumentStore store, IAccessContextProvider accessProvider, IBackgroundTaskQueue taskQueue)
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
            if (result != DocumentWriteStatus.Ok)
            {
                return ResponseDto<bool>.NotFound("Документ не найден.");
            }

            // Новая версия файла — это новый ТЕКСТ документа в корпусе: содержимое актуальных файлов
            // индексируется вместе с карточкой (этап 7 Э4-35). Фоном, как при регистрации и правке —
            // загрузка не ждёт извлечение текста и эмбеддинги; прежняя корпусная версия гасится supersede.
            var documentId = command.DocumentId;
            await taskQueue.EnqueueAsync(
                "Индексация документа в корпус ИИ",
                async (sp, ct) => await sp.GetRequiredService<IDocumentIndexer>().IndexAsync(documentId, ct),
                cancellationToken);

            return ResponseDto<bool>.Ok(true);
        }
    }
}
