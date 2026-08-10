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

/// <summary>Список документов с фильтрами (§3.4).</summary>
public sealed record ListDocumentsQuery(
    string? Text = null,
    DocumentGroup? Group = null,
    int? TypeId = null,
    DocumentAggregatedStatus? AggregatedStatus = null,
    DocumentPriority? Priority = null,
    int? InspectorUserId = null,
    int? DivisionId = null,
    DateOnly? RegDateFrom = null,
    DateOnly? RegDateTo = null,
    int Page = 1,
    int PageSize = 25)
    : IRequest<ResponseDto<DocumentPage>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.Search;

    /// <inheritdoc />
    /// <remarks>
    /// Только критерии фильтра — не содержимое найденных документов (ТБ-032). Номер страницы
    /// в сводку не идёт: листание одной и той же выборки — не новый поиск, и засорять им журнал
    /// значит хоронить в шуме настоящие обращения.
    /// </remarks>
    public string? AuditSummary =>
        $"docflow:documents:list:text={Text ?? "-"};group={Group?.ToString() ?? "-"};"
        + $"type={TypeId?.ToString() ?? "-"};status={AggregatedStatus?.ToString() ?? "-"};"
        + $"division={DivisionId?.ToString() ?? "-"};inspector={InspectorUserId?.ToString() ?? "-"}";

    /// <inheritdoc cref="ListDocumentsQuery" />
    public sealed class Handler(IDocumentStore store, IAccessContextProvider accessProvider)
        : IRequestHandler<ListDocumentsQuery, ResponseDto<DocumentPage>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<DocumentPage>> Handle(
            ListDocumentsQuery query, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(query);

            // Fail-closed (ТБ-020/021): без контекста допуска GetCurrentAsync бросает — список
            // не выдаётся вовсе; решётка применяется в самом запросе хранилища (этап 6.1 Э4-35).
            var access = await accessProvider.GetCurrentAsync(cancellationToken);
            var page = await store.ListAsync(
                new DocumentListFilter(
                    query.Text, query.Group, query.TypeId, query.AggregatedStatus, query.Priority,
                    query.InspectorUserId, query.DivisionId, query.RegDateFrom, query.RegDateTo,
                    query.Page, query.PageSize),
                access,
                cancellationToken);

            return ResponseDto<DocumentPage>.Ok(page, page.TotalCount);
        }
    }
}
