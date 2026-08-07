using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.BackgroundTasks;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.DocFlow.Application.Notifications;
using ISC.AI.Modules.DocFlow.Domain.Enums;
using ISC.AI.Modules.DocFlow.Domain.Services;
using Mediator;
using Microsoft.Extensions.DependencyInjection;

namespace ISC.AI.Modules.DocFlow.Application.Documents;

/// <summary>
/// Сценарии документов и назначений (ТЗ СКИД §3–4) — перенос <c>Features/Documents</c> и
/// <c>Features/Assignments</c> СКИД (Э4-35, этап 3.1). Отличия от исходника — как в
/// <c>DocumentTypeScenarios</c> (MediatR→Mediator, сквозной аудит, порт вместо DbContext);
/// дополнительно: гриф и подразделение ОБЯЗАТЕЛЬНЫ при регистрации (ADR-0017 п.5); файлы
/// переходов/продлений — этап 4 (хранилище файлов); роли «кто может» — этап 6.
/// </summary>

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

/// <summary>Удалить сопутствующее вложение документа (§3.3.1).</summary>
public sealed record DeleteAttachmentCommand(int AttachmentId)
    : IRequest<ResponseDto<bool>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.Modify;

    /// <inheritdoc />
    public string? AuditSummary => $"docflow:attachment:{AttachmentId}:delete";

    /// <inheritdoc cref="DeleteAttachmentCommand" />
    public sealed class Handler(IDocumentStore store, IAccessContextProvider accessProvider)
        : IRequestHandler<DeleteAttachmentCommand, ResponseDto<bool>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<bool>> Handle(
            DeleteAttachmentCommand command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);

            var access = await accessProvider.GetCurrentAsync(cancellationToken);
            var status = await store.DeleteAttachmentAsync(command.AttachmentId, access, cancellationToken);

            return status switch
            {
                DocumentWriteStatus.Ok => ResponseDto<bool>.Ok(true, "Вложение удалено."),
                _ => ResponseDto<bool>.NotFound("Вложение не найдено."),
            };
        }
    }
}

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

/// <summary>Переназначить исполнителя (§4.7): срок и статус сохраняются, меняется только ответственный.</summary>
public sealed record ReassignAssigneeCommand(int AssignmentId, int NewAssigneeUserId, string? Reason)
    : IRequest<ResponseDto<bool>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.Modify;

    /// <inheritdoc />
    public string? AuditSummary => $"docflow:assignment:{AssignmentId}:reassign:{NewAssigneeUserId}";

    /// <inheritdoc cref="ReassignAssigneeCommand" />
    public sealed class Handler(
        IDocumentStore store, IAccessContextProvider accessProvider, DocFlowEventNotifier notifier)
        : IRequestHandler<ReassignAssigneeCommand, ResponseDto<bool>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<bool>> Handle(
            ReassignAssigneeCommand command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);

            var access = await accessProvider.GetCurrentAsync(cancellationToken);
            var result = await store.ReassignAssigneeAsync(
                command.AssignmentId, command.NewAssigneeUserId, command.Reason, access, cancellationToken);

            // Notice отсутствует при успехе = «назначили того же» — уведомлять не о чем.
            if (result is { Status: DocumentWriteStatus.Ok, Notice: { } notice })
            {
                await DocFlowEventNotifier.SafeAsync(() => notifier.AssigneeReassignedAsync(
                    notice, access.NumericSubjectId, cancellationToken));
            }

            return result.Status switch
            {
                DocumentWriteStatus.Ok => ResponseDto<bool>.Ok(true),
                DocumentWriteStatus.NotFound => ResponseDto<bool>.NotFound("Назначение не найдено."),
                DocumentWriteStatus.InvalidTransition =>
                    ResponseDto<bool>.BadRequest(
                        "Исполненное и снятое с контроля назначение не переназначается — это переписывание "
                        + "уже состоявшегося факта. Верните назначение в работу, если исполнение продолжается."),
                DocumentWriteStatus.AssigneeOutsideDivision =>
                    ResponseDto<bool>.BadRequest(
                        "Исполнителю не разрешено подразделение назначения — он не увидел бы этот документ."),
                DocumentWriteStatus.Conflict =>
                    ResponseDto<bool>.BadRequest("Назначение изменено параллельно — обновите данные и повторите."),
                _ => ResponseDto<bool>.Fail("Не удалось переназначить исполнителя."),
            };
        }
    }
}

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

/// <summary>
/// Сменить статус назначения (§4.2): матрица §4.5, «Просрочено» — только система, вход/выход
/// «Контроля» фиксирует/сбрасывает контролёра, переход пишется в историю, агрегат пересчитывается.
/// </summary>
public sealed record ChangeAssignmentStatusCommand(
    int AssignmentId, AssignmentStatus NewStatus, string? Comment,
    IReadOnlyList<UploadedFile>? Files = null)
    : IRequest<ResponseDto<bool>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.Modify;

    /// <inheritdoc />
    public string? AuditSummary => $"docflow:assignment:{AssignmentId}:status:{NewStatus}";

    /// <inheritdoc cref="ChangeAssignmentStatusCommand" />
    public sealed class Handler(
        IDocumentStore store, IAccessContextProvider accessProvider, DocFlowEventNotifier notifier)
        : IRequestHandler<ChangeAssignmentStatusCommand, ResponseDto<bool>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<bool>> Handle(
            ChangeAssignmentStatusCommand command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);
            var access = await accessProvider.GetCurrentAsync(cancellationToken);

            var result = await store.ChangeAssignmentStatusAsync(
                command.AssignmentId, command.NewStatus, command.Comment,
                access, command.Files, cancellationToken);

            if (result == DocumentWriteStatus.Ok)
            {
                // Участники читаются ПОСЛЕ записи: вход в «Контроль» назначает контролёра, и он тоже
                // должен получить уведомление о собственном назначении контролёром (разд. 5).
                var participants = await store.GetAssignmentParticipantsAsync(
                    command.AssignmentId, access, cancellationToken);
                if (participants is not null)
                {
                    await DocFlowEventNotifier.SafeAsync(() => notifier.AssignmentStatusChangedAsync(
                        participants, command.NewStatus, access.NumericSubjectId, cancellationToken));
                }
            }

            return result switch
            {
                DocumentWriteStatus.Ok => ResponseDto<bool>.Ok(true),
                DocumentWriteStatus.NotFound => ResponseDto<bool>.NotFound("Назначение не найдено."),
                DocumentWriteStatus.OverdueIsAutomatic =>
                    ResponseDto<bool>.BadRequest("«Просрочено» устанавливается системой автоматически (ТЗ §4.2)."),
                DocumentWriteStatus.InvalidTransition =>
                    ResponseDto<bool>.BadRequest("Такой переход статуса не допускается (ТЗ §4.5)."),
                DocumentWriteStatus.Conflict =>
                    ResponseDto<bool>.BadRequest("Назначение изменено параллельно — обновите данные и повторите."),
                _ => ResponseDto<bool>.Fail("Не удалось изменить статус назначения."),
            };
        }
    }
}

/// <summary>Продлить срок назначения (§4.6): основание обязательно, возврат «В работу» автоматический.</summary>
public sealed record ExtendAssignmentDeadlineCommand(
    int AssignmentId, DateOnly NewDeadline, string Reason,
    IReadOnlyList<UploadedFile>? Files = null)
    : IRequest<ResponseDto<bool>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.Modify;

    /// <inheritdoc />
    public string? AuditSummary => $"docflow:assignment:{AssignmentId}:extend:{NewDeadline:yyyy-MM-dd}";

    /// <inheritdoc cref="ExtendAssignmentDeadlineCommand" />
    public sealed class Handler(
        IDocumentStore store, IAccessContextProvider accessProvider, DocFlowEventNotifier notifier)
        : IRequestHandler<ExtendAssignmentDeadlineCommand, ResponseDto<bool>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<bool>> Handle(
            ExtendAssignmentDeadlineCommand command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);
            var access = await accessProvider.GetCurrentAsync(cancellationToken);

            // §4.6: инициатор продления фиксируется обязательно — без числового субъекта не продлеваем.
            if (access.NumericSubjectId is null)
            {
                return ResponseDto<bool>.BadRequest("Продление срока требует аутентифицированного пользователя.");
            }

            // Участники — ДО записи: в тексте уведомления нужен СТАРЫЙ срок, после продления он утрачен.
            var participants = await store.GetAssignmentParticipantsAsync(
                command.AssignmentId, access, cancellationToken);

            var result = await store.ExtendDeadlineAsync(
                command.AssignmentId, command.NewDeadline, command.Reason, access,
                command.Files, cancellationToken);

            if (result == DocumentWriteStatus.Ok && participants is not null)
            {
                await DocFlowEventNotifier.SafeAsync(() => notifier.DeadlineExtendedAsync(
                    participants, command.NewDeadline, access.NumericSubjectId, cancellationToken));
            }

            return result switch
            {
                DocumentWriteStatus.Ok => ResponseDto<bool>.Ok(true),
                DocumentWriteStatus.NotFound => ResponseDto<bool>.NotFound("Назначение не найдено."),
                DocumentWriteStatus.NoDeadline =>
                    ResponseDto<bool>.BadRequest("У назначения нет срока — продлевать нечего."),
                DocumentWriteStatus.InvalidTransition =>
                    ResponseDto<bool>.BadRequest("Назначение снято с контроля — срок не продлевается (ТЗ §4.2)."),
                DocumentWriteStatus.Conflict =>
                    ResponseDto<bool>.BadRequest("Назначение изменено параллельно — обновите данные и повторите."),
                _ => ResponseDto<bool>.Fail("Не удалось продлить срок назначения."),
            };
        }
    }
}
