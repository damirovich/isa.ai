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

/// <summary>
/// Переиндексировать документ в корпусе (этап 7 Э4-35): вручную с карточки — после сбоя фоновой
/// задачи (делегаты не переживают перезапуск) либо для обновления корпуса после правок.
/// Сама индексация аудируется индексатором (<c>Ingest</c>) — команда лишь ставит задачу.
/// </summary>
public sealed record ReindexDocumentCommand(int DocumentId) : IRequest<ResponseDto<bool>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.Modify;

    /// <inheritdoc />
    public string? AuditSummary => $"docflow:document:{DocumentId}:reindex";

    /// <inheritdoc cref="ReindexDocumentCommand" />
    public sealed class Handler(
        IBackgroundTaskQueue taskQueue, IDocumentStore store, IAccessContextProvider accessProvider)
        : IRequestHandler<ReindexDocumentCommand, ResponseDto<bool>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<bool>> Handle(
            ReindexDocumentCommand command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);

            // Постановка задачи — тоже действие над документом: недоступный переиндексировать нельзя
            // (этап 6.6; раньше обработчик не резолвил допуск вовсе и не аудировался).
            var access = await accessProvider.GetCurrentAsync(cancellationToken);
            if (await store.GetAsync(command.DocumentId, access, cancellationToken) is null)
            {
                return ResponseDto<bool>.NotFound("Документ не найден.");
            }

            var documentId = command.DocumentId;
            await taskQueue.EnqueueAsync(
                "Индексация документа в корпус ИИ",
                async (sp, ct) => await sp.GetRequiredService<IDocumentIndexer>().IndexAsync(documentId, ct),
                cancellationToken);
            return ResponseDto<bool>.Ok(true, "Индексация поставлена в очередь.");
        }
    }
}
