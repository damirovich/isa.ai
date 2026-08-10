using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.DocFlow.Application.Features.Notifications;
using ISC.AI.Modules.DocFlow.Domain.Enums;
using ISC.AI.Modules.DocFlow.Domain.Services;
using Mediator;

namespace ISC.AI.Modules.DocFlow.Application.Features.Documents;

/// <summary>Карточка документа с назначениями (§3.2, §4.8).</summary>
public sealed record GetDocumentQuery(int DocumentId) : IRequest<ResponseDto<DocumentDetails>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.View;

    /// <inheritdoc />
    /// <remarks>
    /// Только идентификатор — не содержимое карточки (ShortContent/FullText, ТБ-032). Гриф записи
    /// AuditClassification намеренно НЕ переопределён: успешный ответ возможен только когда допуск
    /// субъекта уже ≥ грифа документа (решётка в <c>DocumentStore.GetAsync</c>), поэтому классификация
    /// по умолчанию (допуск субъекта) автоматически не ниже грифа данных.
    /// </remarks>
    public string? AuditSummary => $"docflow:document:{DocumentId}";

    /// <inheritdoc cref="GetDocumentQuery" />
    public sealed class Handler(IDocumentStore store, IAccessContextProvider accessProvider)
        : IRequestHandler<GetDocumentQuery, ResponseDto<DocumentDetails>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<DocumentDetails>> Handle(
            GetDocumentQuery query, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(query);

            // Документ вне допуска неотличим от несуществующего (ТБ-020/021, решение «404, не 403»
            // раздачи файлов) — то же сообщение «не найден», существование не подтверждается.
            var access = await accessProvider.GetCurrentAsync(cancellationToken);
            var details = await store.GetAsync(query.DocumentId, access, cancellationToken);
            return details is null
                ? ResponseDto<DocumentDetails>.NotFound("Документ не найден.")
                : ResponseDto<DocumentDetails>.Ok(details);
        }
    }
}
