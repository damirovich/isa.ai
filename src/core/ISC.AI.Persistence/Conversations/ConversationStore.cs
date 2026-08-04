using ISC.AI.Abstractions.Conversations;
using ISC.AI.Abstractions.Enums;

namespace ISC.AI.Persistence.Conversations;

/// <summary>
/// Реализация <see cref="IConversationStore"/> над <see cref="CoreDbContext"/>. Контекст — через фабрику
/// на операцию (Blazor Server, ТС-008). ВСЕ операции разграничены по владельцу (<c>subjectId</c>):
/// чужой/несуществующий диалог трактуется как «не найден» (fail-closed) — история диалога содержит ДСП.
/// </summary>
public sealed class ConversationStore(IDbContextFactory<CoreDbContext> contextFactory) : IConversationStore
{
    /// <inheritdoc />
    public async Task<int> CreateAsync(int subjectId, string title, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        var conversation = new ConversationEntity { Title = Trim(title), SubjectId = subjectId, Classification = 0 };
        db.Conversations.Add(conversation);
        await db.SaveChangesAsync(cancellationToken);
        return conversation.Id;
    }

    /// <inheritdoc />
    public async Task<bool> AppendMessageAsync(
        int conversationId,
        int subjectId,
        ConversationMessageRole role,
        string content,
        short classification,
        string? groundingJson,
        CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        // Разграничение по владельцу: диалог должен принадлежать субъекту (иначе — «не найден»).
        var conversation = await db.Conversations
            .FirstOrDefaultAsync(c => c.Id == conversationId && c.SubjectId == subjectId, cancellationToken);
        if (conversation is null)
        {
            return false;
        }

        db.ConversationMessages.Add(new ConversationMessageEntity
        {
            ConversationId = conversationId,
            Role = role,
            Content = content,
            Classification = classification,
            GroundingJson = groundingJson,
        });

        // Гриф диалога = максимум грифов его сообщений; поднимаем время последней активности (сортировка списка).
        if (classification > conversation.Classification)
        {
            conversation.Classification = classification;
        }

        conversation.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ChatTurn>> GetHistoryAsync(
        int conversationId, int subjectId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        // Сообщения только своего (не удалённого — глобальный query-filter) диалога, по возрастанию времени.
        return await db.ConversationMessages
            .Where(m => m.ConversationId == conversationId && m.Conversation!.SubjectId == subjectId)
            .OrderBy(m => m.Id)
            .Select(m => new ChatTurn(m.Role, m.Content))
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ConversationSummary>> ListAsync(
        int subjectId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        return await db.Conversations
            .Where(c => c.SubjectId == subjectId)
            .OrderByDescending(c => c.UpdatedAt ?? c.CreatedAt)
            .Select(c => new ConversationSummary(c.Id, c.Title, c.Classification, c.UpdatedAt ?? c.CreatedAt))
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<bool> RenameAsync(
        int conversationId, int subjectId, string title, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        var conversation = await db.Conversations
            .FirstOrDefaultAsync(c => c.Id == conversationId && c.SubjectId == subjectId, cancellationToken);
        if (conversation is null)
        {
            return false;
        }

        conversation.Title = Trim(title);
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(
        int conversationId, int subjectId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        var conversation = await db.Conversations
            .FirstOrDefaultAsync(c => c.Id == conversationId && c.SubjectId == subjectId, cancellationToken);
        if (conversation is null)
        {
            return false;
        }

        db.Conversations.Remove(conversation); // мягкое удаление (перехват в AuditedDbContext → IsDeleted = true)
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    // Заголовок диалога — короткий (обрезаем длинный первый запрос до разумной длины столбца title).
    private static string Trim(string title)
    {
        var normalized = string.IsNullOrWhiteSpace(title) ? "Новый диалог" : title.Trim();
        return normalized.Length <= 200 ? normalized : normalized[..200];
    }
}
