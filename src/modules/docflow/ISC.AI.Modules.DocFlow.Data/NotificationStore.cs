using System.Text.Json;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.DocFlow.Domain.Entities;
using ISC.AI.Modules.DocFlow.Domain.Services;
using Microsoft.EntityFrameworkCore;

namespace ISC.AI.Modules.DocFlow.Data;

/// <summary>Уведомления (разд. 5 ТЗ СКИД) поверх <see cref="DocFlowDbContext"/>.</summary>
public sealed class NotificationStore(
    IDbContextFactory<DocFlowDbContext> contextFactory,
    IAccessPolicy accessPolicy,
    INotificationSignal signal) : INotificationStore
{
    /// <summary>
    /// Непрочитанные уведомления субъекта, уже суженные допуском: свои по адресату И относящиеся либо
    /// ни к какому документу, либо к ВИДИМОМУ документу. Предикат тот же, что у документов
    /// (решётка ТБ-020/021 + политика профиля ADR-0014) — см. INotificationStore.ListUnreadAsync.
    /// </summary>
    private IQueryable<Notification> VisibleUnread(DocFlowDbContext db, AccessContext access, int recipientUserId)
    {
        var visibleDocuments = db.Documents.VisibleTo(access, accessPolicy).Select(d => d.Id);

        return db.Notifications
            .Where(n => n.RecipientUserId == recipientUserId && !n.IsRead)
            .Where(n => n.DocumentId == null || visibleDocuments.Contains(n.DocumentId.Value));
    }

    /// <inheritdoc />
    public async Task<int> RaiseAsync(NotificationDraft draft, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);

        var recipients = draft.RecipientUserIds.Where(id => id > 0).Distinct().ToList();
        if (recipients.Count == 0)
        {
            return 0;
        }

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        // Дедупликация уведомлений о сроках — по (назначение, вид, значение срока), см. INotificationStore.
        if (draft.AboutDeadline is { } deadline && draft.AssignmentId is { } assignmentId)
        {
            var alreadySent = await db.Notifications.AsNoTracking().AnyAsync(
                n => n.AssignmentId == assignmentId && n.Type == draft.Type && n.AboutDeadline == deadline,
                cancellationToken);
            if (alreadySent)
            {
                return 0;
            }
        }

        var argumentsJson = JsonSerializer.Serialize(draft.Arguments);
        foreach (var recipientUserId in recipients)
        {
            db.Notifications.Add(new Notification
            {
                RecipientUserId = recipientUserId,
                Type = draft.Type,
                MessageKey = draft.MessageKey,
                ArgumentsJson = argumentsJson,
                DocumentId = draft.DocumentId,
                AssignmentId = draft.AssignmentId,
                CommentId = draft.CommentId,
                AboutDeadline = draft.AboutDeadline,
            });
        }

        await db.SaveChangesAsync(cancellationToken);

        // Живое обновление (INotificationSignal): будим открытые страницы получателей ПОСЛЕ записи.
        // Через шину идёт только «перечитай» — содержимое подписчики берут запросом через решётку.
        signal.Publish(recipients);
        return recipients.Count;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<NotificationItem>> ListUnreadAsync(
        AccessContext access, int maxCount = 50, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(access);

        // Fail-closed: без числового субъекта адресата не определить — лента пуста, а не «всё подряд».
        if (access.NumericSubjectId is not { } recipientUserId)
        {
            return [];
        }

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        var rows = await VisibleUnread(db, access, recipientUserId).AsNoTracking()
            .OrderByDescending(n => n.CreatedAt).ThenByDescending(n => n.Id)
            .Take(maxCount)
            .Select(n => new
            {
                n.Id, n.Type, n.MessageKey, n.ArgumentsJson, n.DocumentId, n.CommentId, n.IsRead, n.CreatedAt,
            })
            .ToListAsync(cancellationToken);

        return rows.Select(r => new NotificationItem(
            r.Id,
            r.Type,
            NotificationTemplates.Render(r.MessageKey, Deserialize(r.ArgumentsJson)),
            r.DocumentId,
            // ВАЖНО: CommentId отдаётся ЧЕСТНО. В СКИД чтение из БД жёстко ставило null (осталось от
            // ранней фазы), из-за чего переход к комментарию работал только на «живом» уведомлении и
            // ломался после перезагрузки страницы — дефект переносить незачем.
            r.CommentId,
            r.IsRead,
            r.CreatedAt))
            .ToList();
    }

    /// <inheritdoc />
    public async Task<int> CountUnreadAsync(AccessContext access, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(access);

        if (access.NumericSubjectId is not { } recipientUserId)
        {
            return 0;
        }

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await VisibleUnread(db, access, recipientUserId).AsNoTracking().CountAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<bool> MarkReadAsync(
        int notificationId, int recipientUserId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        // Чужое уведомление не находится по этому же условию — «не ваше» неотличимо от «нет такого».
        var notification = await db.Notifications
            .FirstOrDefaultAsync(n => n.Id == notificationId && n.RecipientUserId == recipientUserId, cancellationToken);
        if (notification is null)
        {
            return false;
        }

        if (notification.IsRead)
        {
            return true;
        }

        notification.IsRead = true;
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <inheritdoc />
    public async Task MarkAllReadAsync(int recipientUserId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await db.Notifications
            .Where(n => n.RecipientUserId == recipientUserId && !n.IsRead)
            .ExecuteUpdateAsync(set => set.SetProperty(n => n.IsRead, true), cancellationToken);
    }

    // Битый JSON не должен ронять всю ленту — уведомление покажется по шаблону без подстановок.
    private static Dictionary<string, string>? Deserialize(string argumentsJson)
    {
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(argumentsJson);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
