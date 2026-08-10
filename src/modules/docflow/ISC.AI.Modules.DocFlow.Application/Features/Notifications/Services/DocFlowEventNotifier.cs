using ISC.AI.Modules.DocFlow.Domain.Enums;
using ISC.AI.Modules.DocFlow.Domain.Services;
using System.Globalization;

namespace ISC.AI.Modules.DocFlow.Application.Features.Notifications;

/// <summary>
/// Превращает события документооборота в уведомления (разд. 5 ТЗ СКИД): кому и с каким текстом.
/// Живёт в слое сценариев, а не в хранилище: «кто получатель» — прикладное правило, и оно должно
/// меняться без переписывания SQL-слоя.
/// </summary>
/// <remarks>
/// Два сквозных правила, оба перенесены из СКИД:
/// 1) ИНИЦИАТОР СЕБЯ НЕ УВЕДОМЛЯЕТ — он только что сам совершил действие;
/// 2) один человек получает по событию НЕ БОЛЕЕ ОДНОГО уведомления, даже если он одновременно
///    исполнитель, инспектор и контролёр (дубли отсекаются на уровне списка получателей).
///
/// Сбой уведомления НЕ откатывает само действие: уведомление вспомогательно, а команда уже
/// выполнена и зафиксирована в аудите. Поэтому вызывающие сценарии оборачивают вызовы в try/catch —
/// см. <see cref="SafeAsync"/>.
/// </remarks>
public sealed class DocFlowEventNotifier(INotificationStore notifications, IUserDirectory users)
{
    /// <summary>
    /// Уведомления о созданных при регистрации назначениях (§4.1): исполнителю — «вам назначено»,
    /// инспектору документа — «создано назначение».
    /// </summary>
    /// <remarks>
    /// На вход идёт <see cref="CreatedDocumentNotice"/> ИЗ операции создания, а не перечитанная
    /// карточка: иначе уведомления молча пропадали бы для документов, которых сам регистратор не видит
    /// (сужающая политика профиля по роли, ADR-0014) — см. комментарий у самого типа.
    /// </remarks>
    public async Task DocumentRegisteredAsync(
        CreatedDocumentNotice document, int? actorUserId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);

        var title = document.DocumentTitle;

        foreach (var assignment in document.Assignments)
        {
            var recipients = Exclude([assignment.AssigneeUserId], actorUserId);
            if (recipients.Count > 0)
            {
                await notifications.RaiseAsync(
                    new NotificationDraft(
                        recipients,
                        NotificationType.Assigned,
                        NotificationTemplates.AssignedToYou,
                        new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["document"] = title,
                            ["deadline"] = FormatDeadline(assignment.Deadline),
                        },
                        document.DocumentId,
                        assignment.AssignmentId),
                    cancellationToken);
            }

            // Инспектор — наблюдатель: другой текст, но тот же вид уведомления (решение СКИД).
            // Если инспектор сам же и исполнитель, он уже получил «вам назначено» — не дублируем.
            var watchers = Exclude([document.InspectorUserId], actorUserId, assignment.AssigneeUserId);
            if (watchers.Count > 0)
            {
                await notifications.RaiseAsync(
                    new NotificationDraft(
                        watchers,
                        NotificationType.Assigned,
                        NotificationTemplates.AssignedNotice,
                        new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["document"] = title,
                            ["deadline"] = FormatDeadline(assignment.Deadline),
                        },
                        document.DocumentId,
                        assignment.AssignmentId),
                    cancellationToken);
            }
        }
    }

    /// <summary>
    /// Уведомления о смене исполнителя (§4.7): новому — «вы назначены», прежнему — «передано другому»,
    /// инспектору документа — «сменился исполнитель».
    /// </summary>
    /// <remarks>
    /// Три РАЗНЫХ текста одного вида — перенос решения СКИД. Отличия: там инициатор себя из получателей
    /// не исключал (их DL-066) и в наблюдатели попадали ВСЕ Руководители; здесь действуют наши сквозные
    /// правила — инициатор себя не уведомляет, один человек получает не больше одного уведомления,
    /// а «все Руководители» недоступны модулю (список ролей ведёт профиль, ADR-0017).
    /// </remarks>
    public async Task AssigneeReassignedAsync(
        ReassignedNotice notice, int? actorUserId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(notice);

        var actorName = await NameOfAsync(actorUserId, cancellationToken);
        var deadline = FormatDeadline(notice.Deadline);

        var newAssignee = Exclude([notice.NewAssigneeUserId], actorUserId);
        if (newAssignee.Count > 0)
        {
            await notifications.RaiseAsync(
                new NotificationDraft(
                    newAssignee,
                    NotificationType.Reassigned,
                    NotificationTemplates.ReassignedToYou,
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["document"] = notice.DocumentTitle,
                        ["deadline"] = deadline,
                    },
                    notice.DocumentId,
                    notice.AssignmentId),
                cancellationToken);
        }

        // Прежнего исполнителя уведомляем, только если он БЫЛ и это не тот же человек: назначение
        // могло быть «на подразделение», без лица (перенос условия СКИД).
        var previous = Exclude([notice.PreviousAssigneeUserId], actorUserId, notice.NewAssigneeUserId);
        if (previous.Count > 0)
        {
            await notifications.RaiseAsync(
                new NotificationDraft(
                    previous,
                    NotificationType.Reassigned,
                    NotificationTemplates.ReassignedFromYou,
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["document"] = notice.DocumentTitle,
                        ["actor"] = actorName,
                    },
                    notice.DocumentId,
                    notice.AssignmentId),
                cancellationToken);
        }

        // Инспектор — наблюдатель; если он же новый или прежний исполнитель, он уже уведомлён.
        var watchers = Exclude(
            [notice.InspectorUserId], actorUserId, notice.NewAssigneeUserId, notice.PreviousAssigneeUserId);
        if (watchers.Count > 0)
        {
            await notifications.RaiseAsync(
                new NotificationDraft(
                    watchers,
                    NotificationType.Reassigned,
                    NotificationTemplates.ReassignedNotice,
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["document"] = notice.DocumentTitle,
                        ["actor"] = actorName,
                    },
                    notice.DocumentId,
                    notice.AssignmentId),
                cancellationToken);
        }
    }

    /// <summary>Уведомление о смене статуса назначения (§4.2): исполнителю, инспектору и контролёру.</summary>
    public async Task AssignmentStatusChangedAsync(
        AssignmentParticipants participants,
        AssignmentStatus newStatus,
        int? actorUserId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(participants);

        var recipients = Exclude(
            [participants.AssigneeUserId, participants.InspectorUserId, participants.ControllerUserId],
            actorUserId);
        if (recipients.Count == 0)
        {
            return;
        }

        await notifications.RaiseAsync(
            new NotificationDraft(
                recipients,
                NotificationType.StatusChanged,
                NotificationTemplates.StatusChanged,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["document"] = participants.DocumentTitle,
                    ["status"] = AssignmentStatusNames.Of(newStatus),
                    ["actor"] = await NameOfAsync(actorUserId, cancellationToken),
                },
                participants.DocumentId,
                participants.AssignmentId),
            cancellationToken);
    }

    /// <summary>Уведомление о продлении срока (§4.6): старый срок берётся ДО записи продления.</summary>
    public async Task DeadlineExtendedAsync(
        AssignmentParticipants participantsBeforeExtension,
        DateOnly newDeadline,
        int? actorUserId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(participantsBeforeExtension);

        var recipients = Exclude(
            [
                participantsBeforeExtension.AssigneeUserId,
                participantsBeforeExtension.InspectorUserId,
                participantsBeforeExtension.ControllerUserId,
            ],
            actorUserId);
        if (recipients.Count == 0)
        {
            return;
        }

        await notifications.RaiseAsync(
            new NotificationDraft(
                recipients,
                NotificationType.DeadlineExtended,
                NotificationTemplates.DeadlineExtended,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["document"] = participantsBeforeExtension.DocumentTitle,
                    ["old"] = FormatDeadline(participantsBeforeExtension.Deadline),
                    ["new"] = FormatDeadline(newDeadline),
                },
                participantsBeforeExtension.DocumentId,
                participantsBeforeExtension.AssignmentId),
            cancellationToken);
    }

    /// <summary>
    /// Уведомления о новом комментарии (§4.8): упомянутым — «вас упомянули», остальным участникам
    /// документа (инспектор, исполнители) — «добавлен комментарий».
    /// </summary>
    /// <remarks>
    /// Упомянутый получает ТОЛЬКО «вас упомянули», даже если он ещё и исполнитель: два уведомления об
    /// одном комментарии — шум. Самоупоминание игнорируется (упомянуть себя в СКИД не запрещено).
    /// </remarks>
    /// <summary>
    /// Смена ответственного инспектора документа при правке карточки (§3.2, разд. 5).
    /// </summary>
    /// <remarks>
    /// Уведомляются ОБА: и новый инспектор («вам поручено»), и прежний («с вас снято»). Уведомить
    /// только нового значило бы, что человек молча перестал отвечать за документ и узнал об этом
    /// случайно. Прочие реквизиты правки уведомлений не порождают: их фиксирует журнал аудита,
    /// а лента должна оставаться списком дел, а не потоком мелких изменений.
    /// </remarks>
    public async Task InspectorChangedAsync(
        UpdatedDocumentNotice notice, int actorUserId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(notice);

        var actorName = await NameOfAsync(actorUserId, cancellationToken);
        var arguments = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["document"] = notice.DocumentTitle,
            ["actor"] = actorName,
        };

        // Себе уведомление не шлём: тот, кто правит, и так знает, что сделал.
        var assigned = Exclude([notice.InspectorUserId], actorUserId);
        if (assigned.Count > 0)
        {
            await notifications.RaiseAsync(
                new NotificationDraft(
                    assigned,
                    NotificationType.InspectorChanged,
                    NotificationTemplates.InspectorAssignedToYou,
                    arguments,
                    notice.DocumentId),
                cancellationToken);
        }

        var removed = Exclude([notice.PreviousInspectorUserId], actorUserId);
        if (removed.Count > 0)
        {
            await notifications.RaiseAsync(
                new NotificationDraft(
                    removed,
                    NotificationType.InspectorChanged,
                    NotificationTemplates.InspectorRemovedFromYou,
                    arguments,
                    notice.DocumentId),
                cancellationToken);
        }
    }

    public async Task CommentAddedAsync(
        DocumentDetails document,
        int commentId,
        int authorUserId,
        IReadOnlyList<int> mentionedUserIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(mentionedUserIds);

        var title = NotificationTemplates.DocumentTitle(document.RegNumber, document.ShortContent);
        var authorName = await NameOfAsync(authorUserId, cancellationToken);
        var arguments = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["document"] = title,
            ["author"] = authorName,
        };

        var mentioned = Exclude([.. mentionedUserIds.Select(id => (int?)id)], authorUserId);
        if (mentioned.Count > 0)
        {
            await notifications.RaiseAsync(
                new NotificationDraft(
                    mentioned,
                    NotificationType.MentionedInComment,
                    NotificationTemplates.MentionedInComment,
                    arguments,
                    document.Id,
                    AssignmentId: null,
                    CommentId: commentId),
                cancellationToken);
        }

        var participants = new List<int?> { document.InspectorUserId };
        participants.AddRange(document.Assignments.Select(a => a.AssigneeUserId));

        // Уже уведомлённые упоминанием исключаются: одно событие — одно уведомление на человека.
        var alreadyNotified = new List<int?> { authorUserId };
        alreadyNotified.AddRange(mentioned.Select(id => (int?)id));

        var others = Exclude(participants, [.. alreadyNotified]);
        if (others.Count > 0)
        {
            await notifications.RaiseAsync(
                new NotificationDraft(
                    others,
                    NotificationType.CommentAdded,
                    NotificationTemplates.CommentAdded,
                    arguments,
                    document.Id,
                    AssignmentId: null,
                    CommentId: commentId),
                cancellationToken);
        }
    }

    /// <summary>
    /// Выполняет отправку уведомлений так, чтобы её сбой не отменял уже совершённое действие.
    /// Возвращает признак успеха — вызывающему он нужен лишь для логирования, не для ответа
    /// пользователю (команда выполнена, что бы ни случилось с уведомлением).
    /// </summary>
    public static async Task<bool> SafeAsync(Func<Task> send)
    {
        ArgumentNullException.ThrowIfNull(send);

        try
        {
            await send();
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>Уникальные получатели без пустых и без исключённых (инициатор, уже уведомлённые).</summary>
    private static List<int> Exclude(IReadOnlyList<int?> candidates, params int?[] excluded)
    {
        var skip = excluded.Where(id => id is > 0).Select(id => id!.Value).ToHashSet();
        return candidates
            .Where(id => id is > 0 && !skip.Contains(id.Value))
            .Select(id => id!.Value)
            .Distinct()
            .ToList();
    }

    private async Task<string> NameOfAsync(int? userId, CancellationToken cancellationToken)
    {
        if (userId is not { } id || id <= 0)
        {
            // Действие системы (авто-переходы) либо неопознанный субъект — текст не должен пустеть.
            return "система";
        }

        return await users.GetNameAsync(id, cancellationToken) ?? $"пользователь #{id}";
    }

    private static string FormatDeadline(DateOnly? deadline) =>
        deadline is { } value ? value.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture) : "не задан";
}
