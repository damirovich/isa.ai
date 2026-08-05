using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.BackgroundTasks;
using ISC.AI.Abstractions.Security;
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
        IDocumentStore store, IAccessContextProvider accessProvider, IBackgroundTaskQueue taskQueue)
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
                draft, command.Assignments, command.UseCommonDeadline, command.CommonDeadline, cancellationToken);

            if (result.Status == DocumentWriteStatus.Ok)
            {
                // Индексация в корпус — ФОНОМ (этап 7 Э4-35): регистрация не ждёт эмбеддинги.
                // Делегат получает свежий scope; сервисы резолвятся из него, не захватываются.
                var documentId = result.DocumentId;
                await taskQueue.EnqueueAsync(
                    "Индексация документа в корпус ИИ",
                    async (sp, ct) => await sp.GetRequiredService<IDocumentIndexer>().IndexAsync(documentId, ct),
                    cancellationToken);
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
                _ => ResponseDto<int>.Fail("Не удалось зарегистрировать документ."),
            };
        }
    }
}

/// <summary>Список документов с фильтрами (§3.4).</summary>
public sealed record ListDocumentsQuery(
    string? Text = null,
    int? TypeId = null,
    DocumentAggregatedStatus? AggregatedStatus = null,
    DateOnly? RegDateFrom = null,
    DateOnly? RegDateTo = null) : IRequest<ResponseDto<IReadOnlyList<DocumentListItem>>>
{
    /// <inheritdoc cref="ListDocumentsQuery" />
    public sealed class Handler(IDocumentStore store, IAccessContextProvider accessProvider)
        : IRequestHandler<ListDocumentsQuery, ResponseDto<IReadOnlyList<DocumentListItem>>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<IReadOnlyList<DocumentListItem>>> Handle(
            ListDocumentsQuery query, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(query);

            // Fail-closed (ТБ-020/021): без контекста допуска GetCurrentAsync бросает — список
            // не выдаётся вовсе; решётка применяется в самом запросе хранилища (этап 6.1 Э4-35).
            var access = await accessProvider.GetCurrentAsync(cancellationToken);
            var items = await store.ListAsync(
                new DocumentListFilter(query.Text, query.TypeId, query.AggregatedStatus,
                    query.RegDateFrom, query.RegDateTo),
                access,
                cancellationToken);
            return ResponseDto<IReadOnlyList<DocumentListItem>>.Ok(items, items.Count);
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
                command.Language, access.NumericSubjectId, cancellationToken);
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
                access.NumericSubjectId, cancellationToken);
            return result == DocumentWriteStatus.Ok
                ? ResponseDto<bool>.Ok(true)
                : ResponseDto<bool>.NotFound("Документ не найден.");
        }
    }
}

/// <summary>
/// Переиндексировать документ в корпусе (этап 7 Э4-35): вручную с карточки — после сбоя фоновой
/// задачи (делегаты не переживают перезапуск) либо для обновления корпуса после правок.
/// Сама индексация аудируется индексатором (<c>Ingest</c>) — команда лишь ставит задачу.
/// </summary>
public sealed record ReindexDocumentCommand(int DocumentId) : IRequest<ResponseDto<bool>>
{
    /// <inheritdoc cref="ReindexDocumentCommand" />
    public sealed class Handler(IBackgroundTaskQueue taskQueue)
        : IRequestHandler<ReindexDocumentCommand, ResponseDto<bool>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<bool>> Handle(
            ReindexDocumentCommand command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);
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
public sealed record GetDocumentQuery(int DocumentId) : IRequest<ResponseDto<DocumentDetails>>
{
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
    public sealed class Handler(IDocumentStore store, IAccessContextProvider accessProvider)
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
                access.NumericSubjectId, command.Files, cancellationToken);

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
    public sealed class Handler(IDocumentStore store, IAccessContextProvider accessProvider)
        : IRequestHandler<ExtendAssignmentDeadlineCommand, ResponseDto<bool>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<bool>> Handle(
            ExtendAssignmentDeadlineCommand command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);
            var access = await accessProvider.GetCurrentAsync(cancellationToken);

            // §4.6: инициатор продления фиксируется обязательно — без числового субъекта не продлеваем.
            if (access.NumericSubjectId is not { } initiatorId)
            {
                return ResponseDto<bool>.BadRequest("Продление срока требует аутентифицированного пользователя.");
            }

            var result = await store.ExtendDeadlineAsync(
                command.AssignmentId, command.NewDeadline, command.Reason, initiatorId,
                command.Files, cancellationToken);

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
