using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.BackgroundTasks;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.DocFlow.Application.Features.Notifications;
using ISC.AI.Modules.DocFlow.Domain.Enums;
using ISC.AI.Modules.DocFlow.Domain.Services;
using Mediator;
using Microsoft.Extensions.DependencyInjection;

namespace ISC.AI.Modules.DocFlow.Application.Features.Assignments;

/// <summary>
/// Сценарии НАЗНАЧЕНИЙ (ТЗ СКИД §4): добавление §4.1, смена статуса §4.2/§4.5, продление §4.6,
/// переназначение §4.7, лента событий §4.8. Выделены из DocumentScenarios по агрегату (2026-08-10) —
/// тем же разрезом, что partial-файлы DocumentStore.
/// </summary>

/// <summary>Добавить назначение к уже зарегистрированному документу (§4.1).</summary>
public sealed record AddAssignmentCommand(int DocumentId, int DivisionId, int? AssigneeUserId, DateOnly? Deadline)
    : IRequest<ResponseDto<int>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.Modify;

    /// <inheritdoc />
    public string? AuditSummary => $"docflow:document:{DocumentId}:assignment:add:division={DivisionId}";

    /// <inheritdoc cref="AddAssignmentCommand" />
    public sealed class Handler(
        IDocumentStore store, IAccessContextProvider accessProvider, DocFlowEventNotifier notifier)
        : IRequestHandler<AddAssignmentCommand, ResponseDto<int>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<int>> Handle(
            AddAssignmentCommand command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);

            var access = await accessProvider.GetCurrentAsync(cancellationToken);
            var result = await store.AddAssignmentAsync(
                command.DocumentId,
                new AssignmentDraft(command.DivisionId, command.AssigneeUserId, command.Deadline),
                access,
                cancellationToken);

            if (result is { Status: DocumentWriteStatus.Ok, Notice: { } notice })
            {
                // Тот же путь, что при регистрации: исполнителю — «вам назначено», инспектору —
                // «создано назначение». В СКИД уведомления слались ТОЛЬКО при указанном исполнителе,
                // из-за чего назначение «на подразделение» проходило совсем молча.
                await DocFlowEventNotifier.SafeAsync(() => notifier.DocumentRegisteredAsync(
                    notice, access.NumericSubjectId, cancellationToken));
            }

            return result.Status switch
            {
                DocumentWriteStatus.Ok => ResponseDto<int>.Ok(result.AssignmentId),
                DocumentWriteStatus.NotFound => ResponseDto<int>.NotFound("Документ не найден."),
                DocumentWriteStatus.NotExecutionGroup =>
                    ResponseDto<int>.BadRequest(
                        "Назначения возможны только у документов группы «Исполнение» (ТЗ §4.1)."),
                DocumentWriteStatus.AssignmentDivisionTaken =>
                    ResponseDto<int>.BadRequest(
                        "У документа уже есть назначение на это подразделение — выберите другое."),
                DocumentWriteStatus.AssigneeOutsideDivision =>
                    ResponseDto<int>.BadRequest(
                        "Исполнителю не разрешено это подразделение — он не увидел бы порученный документ."),
                _ => ResponseDto<int>.Fail("Не удалось добавить назначение."),
            };
        }
    }
}
