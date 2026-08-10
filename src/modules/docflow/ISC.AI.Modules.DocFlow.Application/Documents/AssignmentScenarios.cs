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
