using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.BackgroundTasks;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.DocFlow.Application.Features.Assignments;
using ISC.AI.Modules.DocFlow.Application.Features.Notifications;
using ISC.AI.Modules.DocFlow.Domain.Enums;
using ISC.AI.Modules.DocFlow.Domain.Services;
using Mediator;
using Microsoft.Extensions.DependencyInjection;

namespace ISC.AI.Modules.DocFlow.Application.Features.Documents;

/// <summary>
/// Изменить реквизиты зарегистрированного документа (§3.2).
/// </summary>
/// <remarks>
/// Назначения этой командой не меняются — у них свои сценарии (<see cref="AddAssignmentCommand"/>,
/// <see cref="ReassignAssigneeCommand"/>, §4.1/§4.7). Правка ВСЕГДА ставит документ в очередь на
/// переиндексацию: в корпусе ИИ лежит его текст, и без этого поиск продолжал бы отвечать по старой
/// редакции (этап 7 Э4-35 — «переиндексация при изменении»).
/// </remarks>
public sealed record UpdateDocumentCommand(
    int DocumentId,
    string? RegNumber,
    DateOnly RegDate,
    int TypeId,
    DocumentDirection Direction,
    string? Source,
    string ShortContent,
    string? FullText,
    string? Notes,
    DocumentPriority? Priority,
    int? InspectorUserId,
    short Classification,
    int DivisionId) : IRequest<ResponseDto<bool>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.Modify;

    /// <inheritdoc />
    public string? AuditSummary => $"docflow:document:{DocumentId}:update";

    /// <inheritdoc />
    /// <remarks>Гриф записи журнала — не ниже грифа правимого документа (ТБ-032).</remarks>
    public short? AuditClassification => Classification;

    /// <inheritdoc cref="UpdateDocumentCommand" />
    public sealed class Handler(
        IDocumentStore store, IAccessContextProvider accessProvider, IBackgroundTaskQueue taskQueue,
        DocFlowEventNotifier notifier, IAuditWriter audit)
        : IRequestHandler<UpdateDocumentCommand, ResponseDto<bool>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<bool>> Handle(
            UpdateDocumentCommand command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);
            var access = await accessProvider.GetCurrentAsync(cancellationToken);

            var edit = new DocumentEdit(
                command.DocumentId,
                command.RegNumber,
                command.RegDate,
                command.TypeId,
                command.Direction,
                command.Source,
                command.ShortContent,
                command.FullText,
                command.Notes,
                command.Priority,
                command.InspectorUserId,
                command.Classification,
                command.DivisionId);

            var result = await store.UpdateAsync(edit, access, cancellationToken);

            if (result is { Status: DocumentWriteStatus.Ok, Notice: { } notice })
            {
                // ЧТО ИМЕННО изменили — отдельной записью журнала, сверх сквозного аудита команды
                // (тот фиксирует лишь факт вызова). Пишутся ИМЕНА полей, не значения: копия
                // содержания под грифом превратила бы журнал во вторую базу документов (ТБ-032).
                await audit.WriteAsync(
                    new AuditEntry(
                        AuditAction.Modify,
                        notice.Classification,
                        access.NumericSubjectId,
                        $"docflow:document:{notice.DocumentId}",
                        notice.DivisionId,
                        notice.ChangedFields.Count == 0
                            ? "правка без изменений"
                            : "изменены реквизиты: " + string.Join(", ", notice.ChangedFields)),
                    cancellationToken);

                // Переиндексация — фоном, как и при регистрации: правка не ждёт эмбеддинги.
                var documentId = notice.DocumentId;
                await taskQueue.EnqueueAsync(
                    "Переиндексация документа в корпус ИИ",
                    async (sp, ct) => await sp.GetRequiredService<IDocumentIndexer>().IndexAsync(documentId, ct),
                    cancellationToken);

                // Уведомление шлём ТОЛЬКО при реальной смене инспектора — прочие правки лента не
                // показывает (иначе она превратится в поток мелких изменений вместо списка дел).
                if (notice.PreviousInspectorUserId != notice.InspectorUserId)
                {
                    await DocFlowEventNotifier.SafeAsync(() => notifier.InspectorChangedAsync(
                        notice, access.NumericSubjectId ?? 0, cancellationToken));
                }
            }

            return result.Status switch
            {
                DocumentWriteStatus.Ok => ResponseDto<bool>.Ok(true, "Документ сохранён."),
                DocumentWriteStatus.NotFound => ResponseDto<bool>.NotFound("Документ не найден."),
                DocumentWriteStatus.TypeUnavailable =>
                    ResponseDto<bool>.BadRequest("Тип документа не найден или выведен из обращения."),
                DocumentWriteStatus.RegNumberTaken =>
                    ResponseDto<bool>.BadRequest("Документ с таким регистрационным номером уже зарегистрирован."),
                DocumentWriteStatus.ExecutionFieldsMissing =>
                    ResponseDto<bool>.BadRequest(
                        "Для документа группы «Исполнение» обязательны приоритет и ответственный инспектор (ТЗ §3.2)."),
                DocumentWriteStatus.TypeGroupChangeNotAllowed =>
                    ResponseDto<bool>.BadRequest(
                        "Нельзя сменить группу документа («Исполнение» ↔ «Хранение») правкой: у документа "
                        + "уже есть поручения либо их отсутствие определено его группой. Зарегистрируйте документ заново."),
                DocumentWriteStatus.ClassificationDowngradeNotAllowed =>
                    ResponseDto<bool>.BadRequest(
                        "Понизить гриф документа правкой нельзя — это рассекречивание, а не исправление. "
                        + "Обратитесь к процедуре снятия грифа."),
                DocumentWriteStatus.ClassificationOutsideClearance =>
                    ResponseDto<bool>.BadRequest(
                        "Гриф документа выше вашего допуска: такой документ вы бы сразу перестали видеть. "
                        + "Выберите гриф не выше вашего либо запросите повышение допуска."),
                DocumentWriteStatus.DivisionOutsideClearance =>
                    ResponseDto<bool>.BadRequest(
                        "Подразделение-владелец не входит в разрешённые вам: такой документ вы бы сразу "
                        + "перестали видеть. Выберите подразделение из своих либо запросите расширение допуска."),
                DocumentWriteStatus.Conflict =>
                    ResponseDto<bool>.Conflict(
                        "Документ изменили, пока форма была открыта. Откройте карточку заново, чтобы не затереть чужую правку."),
                _ => ResponseDto<bool>.Fail("Не удалось сохранить документ."),
            };
        }
    }
}
