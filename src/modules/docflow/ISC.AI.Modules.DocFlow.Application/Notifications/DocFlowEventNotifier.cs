using System.Globalization;
using ISC.AI.Modules.DocFlow.Domain.Enums;
using ISC.AI.Modules.DocFlow.Domain.Services;

namespace ISC.AI.Modules.DocFlow.Application.Notifications;

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
    public async Task DocumentRegisteredAsync(
        DocumentDetails document, int? actorUserId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);

        var title = NotificationTemplates.DocumentTitle(document.RegNumber, document.ShortContent);

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
                        document.Id,
                        assignment.Id),
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
                        document.Id,
                        assignment.Id),
                    cancellationToken);
            }
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
                    ["status"] = StatusNames.Of(newStatus),
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

/// <summary>Русские названия статусов назначения для текстов уведомлений (§4.2).</summary>
internal static class StatusNames
{
    public static string Of(AssignmentStatus status) => status switch
    {
        AssignmentStatus.Registered => "Зарегистрировано",
        AssignmentStatus.InControl => "Контроль",
        AssignmentStatus.InProgress => "В работе",
        AssignmentStatus.PartiallyDone => "Частично исполнено",
        AssignmentStatus.Done => "Исполнено",
        AssignmentStatus.Overdue => "Просрочено",
        AssignmentStatus.Closed => "Снято с контроля",
        _ => status.ToString(),
    };
}
