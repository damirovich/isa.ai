using ISC.AI.Abstractions.Conversations;
using ISC.AI.Abstractions.Enums;

namespace ISC.AI.Persistence.Conversations;

/// <summary>
/// Реализация <see cref="IConversationStore"/> над <see cref="CoreDbContext"/>. Контекст — через фабрику
/// на операцию (Blazor Server, ТС-008). ВСЕ операции разграничены по владельцу (<c>subjectId</c>):
/// чужой/несуществующий диалог трактуется как «не найден» (fail-closed) — история диалога содержит ДСП.
/// </summary>
/// <remarks>
/// Исключение — <see cref="CountScopeAsync"/> и <see cref="PurgeScopeAsync"/>: уничтожение по ключу (ТБ-064, ADR-0052)
/// касается диалогов ЛЮБЫХ владельцев, в том числе скрытых. Их вызывает профиль при уничтожении своего объекта, после
/// проверки права и записи акта в журнал; пользовательских сценариев поверх них нет.
/// </remarks>
public sealed class ConversationStore(IDbContextFactory<CoreDbContext> contextFactory) : IConversationStore
{
    /// <inheritdoc />
    public Task<int> CreateAsync(int subjectId, string title, CancellationToken cancellationToken = default) =>
        CreateAsync(subjectId, title, area: null, classification: 0, scopeKeys: [], cancellationToken);

    /// <inheritdoc />
    public async Task<int> CreateAsync(
        int subjectId,
        string title,
        string? area,
        short classification,
        IReadOnlyCollection<string> scopeKeys,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scopeKeys);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        var conversation = new ConversationEntity
        {
            Title = Trim(title),
            SubjectId = subjectId,
            Classification = classification,
            Area = string.IsNullOrWhiteSpace(area) ? null : area.Trim(),
            ScopeKeys = Keys(scopeKeys),
        };
        db.Conversations.Add(conversation);
        await db.SaveChangesAsync(cancellationToken);
        return conversation.Id;
    }

    /// <inheritdoc />
    public Task<bool> AppendMessageAsync(
        int conversationId,
        int subjectId,
        ConversationMessageRole role,
        string content,
        short classification,
        string? groundingJson,
        CancellationToken cancellationToken = default) =>
        AppendMessageAsync(
            conversationId, subjectId, new ConversationMessageDraft(role, content, classification, groundingJson), cancellationToken);

    /// <inheritdoc />
    public async Task<bool> AppendMessageAsync(
        int conversationId, int subjectId, ConversationMessageDraft message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
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
            Role = message.Role,
            Content = message.Content,
            Classification = message.Classification,
            GroundingJson = message.GroundingJson,
            SourcesJson = message.SourcesJson,
            CheckJson = message.CheckJson,
        });

        // Гриф диалога = максимум грифов его сообщений; поднимаем время последней активности (сортировка списка).
        if (message.Classification > conversation.Classification)
        {
            conversation.Classification = message.Classification;
        }

        // Ключи уничтожения материалов ответа — к ключам диалога (ТБ-064): диалог уйдёт с любым из этих материалов.
        if (message.ScopeKeys is { Count: > 0 } keys)
        {
            var merged = Keys(conversation.ScopeKeys.Concat(keys));
            if (merged.Count != conversation.ScopeKeys.Count)
            {
                conversation.ScopeKeys = merged;
            }
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
    public async Task<ConversationThread?> GetAsync(
        int conversationId, int subjectId, short maxClassification, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        var conversation = await db.Conversations.AsNoTracking()
            .Where(c => c.Id == conversationId && c.SubjectId == subjectId)
            .Select(c => new { c.Id, c.Area, c.Title, c.Classification })
            .FirstOrDefaultAsync(cancellationToken);
        if (conversation is null)
        {
            return null;
        }

        // ТБ-020: сообщение выше ТЕКУЩЕГО допуска не выдаётся (допуск могли понизить после ответа) — только их число.
        var messages = await db.ConversationMessages.AsNoTracking()
            .Where(m => m.ConversationId == conversationId && m.Classification <= maxClassification)
            .OrderBy(m => m.Id)
            .Select(m => new { m.Id, m.Role, m.Content, m.Classification, m.CreatedAt, m.SourcesJson, m.CheckJson })
            .ToListAsync(cancellationToken);
        var hidden = await db.ConversationMessages
            .CountAsync(m => m.ConversationId == conversationId && m.Classification > maxClassification, cancellationToken);

        return new ConversationThread(
            conversation.Id,
            conversation.Area,
            conversation.Title,
            conversation.Classification,
            messages
                .Select(m => new ConversationMessage(
                    m.Id, m.Role, m.Content, m.Classification, Utc(m.CreatedAt), m.SourcesJson, m.CheckJson))
                .ToList(),
            hidden);
    }

    /// <inheritdoc />
    public async Task<ConversationSummary?> FindAsync(
        int conversationId, int subjectId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        var row = await db.Conversations.AsNoTracking()
            .Where(c => c.Id == conversationId && c.SubjectId == subjectId)
            .Select(c => new { c.Id, c.Title, c.Classification, Updated = c.UpdatedAt ?? c.CreatedAt, c.Area })
            .FirstOrDefaultAsync(cancellationToken);
        return row is null ? null : new ConversationSummary(row.Id, row.Title, row.Classification, Utc(row.Updated), row.Area);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ConversationSummary>> ListAsync(
        int subjectId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        return await db.Conversations
            .Where(c => c.SubjectId == subjectId)
            .OrderByDescending(c => c.UpdatedAt ?? c.CreatedAt)
            .Select(c => new ConversationSummary(c.Id, c.Title, c.Classification, c.UpdatedAt ?? c.CreatedAt, c.Area))
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ConversationSummary>> ListInAreaAsync(
        int subjectId, string? area, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        var key = string.IsNullOrWhiteSpace(area) ? null : area.Trim();
        var rows = await db.Conversations.AsNoTracking()
            .Where(c => c.SubjectId == subjectId && c.Area == key)
            .OrderByDescending(c => c.UpdatedAt ?? c.CreatedAt).ThenByDescending(c => c.Id)
            .Select(c => new { c.Id, c.Title, c.Classification, Updated = c.UpdatedAt ?? c.CreatedAt, c.Area })
            .ToListAsync(cancellationToken);
        return rows.Select(r => new ConversationSummary(r.Id, r.Title, r.Classification, Utc(r.Updated), r.Area)).ToList();
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

    /// <inheritdoc />
    public async Task<int> CountScopeAsync(string scopeKey, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeKey);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        // Скрытые («удалённые» сотрудником) диалоги — тоже материал объекта: в акте их видно.
        return await db.Conversations.IgnoreQueryFilters()
            .CountAsync(c => c.ScopeKeys.Contains(scopeKey), cancellationToken);
    }

    /// <inheritdoc />
    public async Task<int> PurgeScopeAsync(string scopeKey, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeKey);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        // ФИЗИЧЕСКОЕ удаление в обход мягкого (ExecuteDelete не проходит через перехват SaveChanges), включая скрытые
        // диалоги; сообщения уходят каскадом внешнего ключа (ТБ-064). Записи журнала о вопросах остаются (ТБ-030).
        return await db.Conversations.IgnoreQueryFilters()
            .Where(c => c.ScopeKeys.Contains(scopeKey))
            .ExecuteDeleteAsync(cancellationToken);
    }

    // Заголовок диалога — короткий (обрезаем длинный первый запрос до разумной длины столбца title).
    private static string Trim(string title)
    {
        var normalized = string.IsNullOrWhiteSpace(title) ? "Новый диалог" : title.Trim();
        return normalized.Length <= 200 ? normalized : normalized[..200];
    }

    // Ключи уничтожения: без пустых и без повторов, порядок первого появления.
    private static List<string> Keys(IEnumerable<string> keys) =>
        keys.Where(k => !string.IsNullOrWhiteSpace(k)).Select(k => k.Trim()).Distinct(StringComparer.Ordinal).ToList();

    // Время из БД — UTC (timestamptz); вид проставляется явно, чтобы ToLocalTime() в интерфейсе был верным.
    private static DateTime Utc(DateTime value) =>
        value.Kind == DateTimeKind.Utc ? value : DateTime.SpecifyKind(value, DateTimeKind.Utc);
}
