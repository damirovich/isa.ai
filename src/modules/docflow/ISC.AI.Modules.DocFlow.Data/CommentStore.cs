using ISC.AI.Modules.DocFlow.Domain.Entities;
using ISC.AI.Modules.DocFlow.Domain.Services;
using ISC.AI.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ISC.AI.Modules.DocFlow.Data;

/// <summary>
/// Комментарии к документу (ТЗ СКИД §4.8) поверх <see cref="DocFlowDbContext"/>. Имена авторов и
/// упомянутых читаются из ядрового реестра <c>core.app_user</c> ОТДЕЛЬНЫМ контекстом (слабая ссылка по
/// значению, без FK через границу схем — ТО-инф-06; право <c>*.Data</c> → <c>Persistence</c>).
/// </summary>
public sealed class CommentStore(
    IDbContextFactory<DocFlowDbContext> contextFactory,
    IDbContextFactory<CoreDbContext> coreContextFactory,
    IDocFlowFileStorage fileStorage) : ICommentStore
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<CommentItem>> ListAsync(
        int documentId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        // Глобальный query-filter ISoftDeletable скрыл бы удалённые целиком — но ветка ответов должна
        // остаться связной (ответ на удалённый комментарий продолжает читаться), поэтому берём ВСЕ и
        // обнуляем содержимое удалённых ниже (перенос контракта СКИД).
        var rows = await db.DocumentComments.AsNoTracking().IgnoreQueryFilters()
            .Where(c => c.DocumentId == documentId)
            .OrderBy(c => c.CreatedAt).ThenBy(c => c.Id)
            .Select(c => new
            {
                c.Id,
                c.ParentCommentId,
                c.AuthorUserId,
                c.Content,
                c.CommentType,
                c.IsResolved,
                c.ResolvedByUserId,
                c.ResolvedAt,
                c.IsEdited,
                c.IsDeleted,
                c.CreatedAt,
                c.UpdatedAt,
                Mentions = c.Mentions.Select(m => m.UserId).ToList(),
                Files = c.Files
                    .Select(f => new CommentFileItem(f.Id, f.FileName, f.ContentType, f.FileSize, f.StoredFileName))
                    .ToList(),
            })
            .ToListAsync(cancellationToken);

        if (rows.Count == 0)
        {
            return [];
        }

        // Имена — одним запросом на всю ленту (авторы + упомянутые), чтобы не ходить в core на каждую строку.
        var userIds = rows.Select(r => r.AuthorUserId)
            .Concat(rows.SelectMany(r => r.Mentions))
            .Distinct()
            .ToList();
        var names = await ResolveUserNamesAsync(userIds, cancellationToken);

        return rows.Select(r => new CommentItem(
            r.Id,
            r.ParentCommentId,
            r.AuthorUserId,
            NameOf(names, r.AuthorUserId),
            r.IsDeleted ? string.Empty : r.Content,
            r.CommentType,
            r.IsResolved,
            r.ResolvedByUserId,
            r.ResolvedAt,
            r.IsEdited,
            r.IsDeleted,
            r.CreatedAt,
            r.UpdatedAt,
            r.IsDeleted ? [] : r.Mentions.Select(id => new CommentMentionItem(id, NameOf(names, id))).ToList(),
            r.IsDeleted ? [] : r.Files))
            .ToList();
    }

    /// <inheritdoc />
    public async Task<(CommentWriteStatus Status, int CommentId)> AddAsync(
        CommentDraft draft, int authorUserId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        if (!await db.Documents.AnyAsync(d => d.Id == draft.DocumentId, cancellationToken))
        {
            return (CommentWriteStatus.NotFound, 0);
        }

        if (draft.ParentCommentId is { } parentId)
        {
            // Родитель обязан принадлежать ТОМУ ЖЕ документу: иначе ответом можно было бы «пришить»
            // ветку к чужому документу (перенос проверки СКИД ParentCommentMismatch).
            var parentDocumentId = await db.DocumentComments.AsNoTracking().IgnoreQueryFilters()
                .Where(c => c.Id == parentId)
                .Select(c => (int?)c.DocumentId)
                .FirstOrDefaultAsync(cancellationToken);
            if (parentDocumentId is null)
            {
                return (CommentWriteStatus.NotFound, 0);
            }

            if (parentDocumentId != draft.DocumentId)
            {
                return (CommentWriteStatus.ParentMismatch, 0);
            }
        }

        var mentions = await ValidateMentionsAsync(draft.Content, draft.MentionedUserIds, cancellationToken);

        var comment = new DocumentComment
        {
            DocumentId = draft.DocumentId,
            AuthorUserId = authorUserId,
            ParentCommentId = draft.ParentCommentId,
            Content = draft.Content,
            CommentType = draft.CommentType,
            Mentions = [.. mentions.Select(id => new DocumentCommentMention { UserId = id })],
        };
        db.DocumentComments.Add(comment);

        // Файлы — в хранилище ДО записи в БД; при сбое БД компенсирующе удаляются (как в остальных
        // файловых операциях модуля, этап 4.2).
        var storedFiles = new List<(string StoredFileName, string SubPath)>();
        try
        {
            if (draft.Files is { Count: > 0 })
            {
                foreach (var file in draft.Files)
                {
                    var subPath = draft.DocumentId.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    using var content = new MemoryStream(file.Content);
                    var storedFileName = await fileStorage.SaveAsync(
                        content, Path.GetExtension(file.FileName), FileCategories.Comments, subPath, cancellationToken);
                    storedFiles.Add((storedFileName, subPath));

                    comment.Files.Add(new DocumentCommentFile
                    {
                        FileName = file.FileName,
                        StoredFileName = storedFileName,
                        ContentType = file.ContentType,
                        FileSize = file.Content.LongLength,
                        UploadedByUserId = authorUserId,
                    });
                }
            }

            await db.SaveChangesAsync(cancellationToken);
            return (CommentWriteStatus.Ok, comment.Id);
        }
        catch
        {
            foreach (var (storedFileName, subPath) in storedFiles)
            {
                await fileStorage.DeleteAsync(storedFileName, FileCategories.Comments, subPath, CancellationToken.None);
            }

            throw;
        }
    }

    /// <inheritdoc />
    public async Task<CommentWriteStatus> UpdateAsync(
        int commentId, string content, IReadOnlyList<int> mentionedUserIds, int editorUserId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(content);
        ArgumentNullException.ThrowIfNull(mentionedUserIds);

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        var comment = await db.DocumentComments.IgnoreQueryFilters()
            .Include(c => c.Mentions)
            .FirstOrDefaultAsync(c => c.Id == commentId, cancellationToken);
        if (comment is null)
        {
            return CommentWriteStatus.NotFound;
        }

        if (comment.AuthorUserId != editorUserId)
        {
            return CommentWriteStatus.NotAuthor;
        }

        if (comment.IsDeleted)
        {
            return CommentWriteStatus.AlreadyDeleted;
        }

        comment.Content = content;
        comment.IsEdited = true;

        // Упоминания пересобираются целиком: правка текста могла и добавить, и убрать токены.
        var mentions = await ValidateMentionsAsync(content, mentionedUserIds, cancellationToken);
        db.DocumentCommentMentions.RemoveRange(comment.Mentions);
        foreach (var userId in mentions)
        {
            db.DocumentCommentMentions.Add(new DocumentCommentMention { CommentId = comment.Id, UserId = userId });
        }

        return await SaveDetectingConflictAsync(db, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<CommentWriteStatus> DeleteAsync(
        int commentId, int actorUserId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        var comment = await db.DocumentComments.IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.Id == commentId, cancellationToken);
        if (comment is null)
        {
            return CommentWriteStatus.NotFound;
        }

        if (comment.AuthorUserId != actorUserId)
        {
            return CommentWriteStatus.NotAuthor;
        }

        // Идемпотентно: повторное удаление — не ошибка (перенос поведения СКИД).
        if (comment.IsDeleted)
        {
            return CommentWriteStatus.Ok;
        }

        comment.IsDeleted = true;
        return await SaveDetectingConflictAsync(db, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<CommentWriteStatus> SetResolvedAsync(
        int commentId, bool resolved, int actorUserId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        var comment = await db.DocumentComments.IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.Id == commentId, cancellationToken);
        if (comment is null)
        {
            return CommentWriteStatus.NotFound;
        }

        // §4.8 (решение СКИД DL-077): закрывается ОБСУЖДЕНИЕ целиком, а не отдельный ответ в ветке.
        if (comment.ParentCommentId is not null)
        {
            return CommentWriteStatus.OnlyRootCanBeResolved;
        }

        if (comment.IsResolved == resolved)
        {
            return CommentWriteStatus.Ok;
        }

        comment.IsResolved = resolved;
        comment.ResolvedByUserId = resolved ? actorUserId : null;
        comment.ResolvedAt = resolved ? DateTime.UtcNow : null;
        return await SaveDetectingConflictAsync(db, cancellationToken);
    }

    private static async Task<CommentWriteStatus> SaveDetectingConflictAsync(
        DocFlowDbContext db, CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return CommentWriteStatus.Ok;
        }
        catch (DbUpdateConcurrencyException)
        {
            return CommentWriteStatus.Conflict;
        }
    }

    /// <summary>
    /// Пересечение трёх источников (перенос решения СКИД): токен есть в САМОМ тексте И пользователь
    /// заявлен клиентом И существует активным в реестре. Ни тексту, ни списку от клиента по отдельности
    /// доверять нельзя — иначе можно «упомянуть» кого угодно, не написав этого в комментарии.
    /// </summary>
    private async Task<IReadOnlyList<int>> ValidateMentionsAsync(
        string content, IReadOnlyList<int> claimedUserIds, CancellationToken cancellationToken)
    {
        var fromText = MentionTextParser.ExtractMentionedUserIds(content);
        if (fromText.Count == 0 || claimedUserIds.Count == 0)
        {
            return [];
        }

        var candidates = fromText.Intersect(claimedUserIds).ToList();
        if (candidates.Count == 0)
        {
            return [];
        }

        await using var coreDb = await coreContextFactory.CreateDbContextAsync(cancellationToken);
        return await coreDb.Users.AsNoTracking()
            .Where(u => candidates.Contains(u.Id) && u.IsActive)
            .Select(u => u.Id)
            .ToListAsync(cancellationToken);
    }

    private async Task<Dictionary<int, string>> ResolveUserNamesAsync(
        IReadOnlyList<int> userIds, CancellationToken cancellationToken)
    {
        await using var coreDb = await coreContextFactory.CreateDbContextAsync(cancellationToken);
        return await coreDb.Users.AsNoTracking()
            .Where(u => userIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.DisplayName ?? u.UserName, cancellationToken);
    }

    private static string NameOf(Dictionary<int, string> names, int userId) =>
        names.TryGetValue(userId, out var name) ? name : $"пользователь №{userId}";
}
