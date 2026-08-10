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

// Перенос из СКИД (Э4-35, этап 3.1) с отличиями: MediatR→Mediator, сквозной аудит, порт вместо
// DbContext; гриф и подразделение ОБЯЗАТЕЛЬНЫ при регистрации (ADR-0017 п.5). Остальные сценарии
// документов и назначений — соседние срезы Features/Documents и Features/Assignments.

/// <summary>Зарегистрировать документ (§3.2); для «Исполнения» — сразу с назначениями (§4.1).</summary>
public sealed record RegisterDocumentCommand(
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
    int DivisionId,
    IReadOnlyList<AssignmentDraft> Assignments,
    bool UseCommonDeadline,
    DateOnly? CommonDeadline) : IRequest<ResponseDto<int>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.Modify;

    /// <inheritdoc />
    public string? AuditSummary => $"docflow:document:create:{RegNumber ?? "без номера"}";

    /// <inheritdoc />
    /// <remarks>Гриф записи журнала — не ниже грифа регистрируемого документа (ТБ-032).</remarks>
    public short? AuditClassification => Classification;

    /// <inheritdoc cref="RegisterDocumentCommand" />
    public sealed class Handler(
        IDocumentStore store, IAccessContextProvider accessProvider, IBackgroundTaskQueue taskQueue,
        DocFlowEventNotifier notifier)
        : IRequestHandler<RegisterDocumentCommand, ResponseDto<int>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<int>> Handle(
            RegisterDocumentCommand command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);
            var access = await accessProvider.GetCurrentAsync(cancellationToken);

            var draft = new DocumentDraft(
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
                command.DivisionId,
                access.NumericSubjectId);

            var result = await store.CreateAsync(
                draft, command.Assignments, command.UseCommonDeadline, command.CommonDeadline, access, cancellationToken);

            if (result.Status == DocumentWriteStatus.Ok)
            {
                // Индексация в корпус — ФОНОМ (этап 7 Э4-35): регистрация не ждёт эмбеддинги.
                // Делегат получает свежий scope; сервисы резолвятся из него, не захватываются.
                var documentId = result.DocumentId;
                await taskQueue.EnqueueAsync(
                    "Индексация документа в корпус ИИ",
                    async (sp, ct) => await sp.GetRequiredService<IDocumentIndexer>().IndexAsync(documentId, ct),
                    cancellationToken);

                // Уведомления о созданных назначениях (§4.1, разд. 5). Данные берутся ИЗ результата
                // создания, а НЕ перечитыванием карточки допуском автора: право исполнителя и
                // инспектора получить уведомление не зависит от того, видит ли документ регистратор
                // (сужающая политика профиля может быть построена по роли — ADR-0014). Перечитывание
                // молча съедало все уведомления такого документа. Регистратор себя не уведомляет.
                if (result.Notice is { } notice)
                {
                    await DocFlowEventNotifier.SafeAsync(() => notifier.DocumentRegisteredAsync(
                        notice, access.NumericSubjectId, cancellationToken));
                }
            }

            return result.Status switch
            {
                DocumentWriteStatus.Ok => ResponseDto<int>.Ok(result.DocumentId),
                DocumentWriteStatus.TypeUnavailable =>
                    ResponseDto<int>.BadRequest("Тип документа не найден или неактивен."),
                DocumentWriteStatus.RegNumberTaken =>
                    ResponseDto<int>.BadRequest("Документ с таким регистрационным номером уже зарегистрирован."),
                DocumentWriteStatus.ExecutionFieldsMissing =>
                    ResponseDto<int>.BadRequest(
                        "Для документа группы «Исполнение» обязательны приоритет, инспектор и хотя бы одно назначение (ТЗ §3.2)."),
                // Два отдельных текста вместо прежнего «гриф ИЛИ подразделение»: раньше пользователю
                // приходилось гадать, какое из двух полей исправлять.
                DocumentWriteStatus.ClassificationOutsideClearance =>
                    ResponseDto<int>.BadRequest(
                        "Гриф документа выше вашего допуска: такой документ вы бы сразу перестали видеть. "
                        + "Выберите гриф не выше вашего либо запросите повышение допуска."),
                DocumentWriteStatus.DivisionOutsideClearance =>
                    ResponseDto<int>.BadRequest(
                        "Подразделение-владелец не входит в разрешённые вам: такой документ вы бы сразу "
                        + "перестали видеть. Выберите подразделение из своих либо запросите расширение допуска."),
                _ => ResponseDto<int>.Fail("Не удалось зарегистрировать документ."),
            };
        }
    }
}
