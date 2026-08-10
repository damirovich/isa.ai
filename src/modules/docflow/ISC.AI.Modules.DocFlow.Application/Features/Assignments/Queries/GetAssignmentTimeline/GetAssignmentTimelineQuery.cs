using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.DocFlow.Application.Features.Notifications;
using ISC.AI.Modules.DocFlow.Domain.Enums;
using ISC.AI.Modules.DocFlow.Domain.Services;
using Mediator;

namespace ISC.AI.Modules.DocFlow.Application.Features.Assignments;

/// <summary>Лента событий назначения (§4.8): переходы статусов и продления сроков одной хронологией.</summary>
public sealed record GetAssignmentTimelineQuery(int AssignmentId)
    : IRequest<ResponseDto<IReadOnlyList<AssignmentTimelineEvent>>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.View;

    /// <inheritdoc />
    /// <remarks>
    /// Только идентификатор — не содержимое ленты (комментарии переходов и основания продлений
    /// в журнал не копируются, ТБ-032). Гриф записи не переопределён по той же причине, что
    /// у карточки: успешный ответ возможен лишь когда допуск субъекта уже ≥ грифа документа.
    /// </remarks>
    public string? AuditSummary => $"docflow:assignment:{AssignmentId}:timeline";

    /// <inheritdoc cref="GetAssignmentTimelineQuery" />
    public sealed class Handler(IDocumentStore store, IAccessContextProvider accessProvider)
        : IRequestHandler<GetAssignmentTimelineQuery, ResponseDto<IReadOnlyList<AssignmentTimelineEvent>>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<IReadOnlyList<AssignmentTimelineEvent>>> Handle(
            GetAssignmentTimelineQuery query, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(query);

            var access = await accessProvider.GetCurrentAsync(cancellationToken);
            var events = await store.GetAssignmentTimelineAsync(query.AssignmentId, access, cancellationToken);

            return events is null
                ? ResponseDto<IReadOnlyList<AssignmentTimelineEvent>>.NotFound("Назначение не найдено.")
                : ResponseDto<IReadOnlyList<AssignmentTimelineEvent>>.Ok(events, events.Count);
        }
    }
}
