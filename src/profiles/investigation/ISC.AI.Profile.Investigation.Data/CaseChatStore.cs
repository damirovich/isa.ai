using System.Text.Json;
using ISC.AI.Abstractions.Enums;
using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Investigation.Domain.Entities;
using ISC.AI.Profile.Investigation.Domain.Services;
using Microsoft.EntityFrameworkCore;

namespace ISC.AI.Profile.Investigation.Data;

/// <summary>
/// Диалоги чата по материалам дел поверх <c>investigation.case_chat</c> и <c>case_chat_message</c> (ADR-0051).
/// </summary>
/// <remarks>
/// ИНВАРИАНТ (ТБ-020/021/071): каждая операция — только над СВОИМ диалогом (<c>owner_user_id</c> = вошедший сотрудник),
/// у диалога по делу — ещё и при видимости дела по полной решётке (<see cref="CaseAccessRule.Apply"/>). Чужой диалог,
/// диалог дела вне доступа и несуществующий неразличимы. Без права «Дашборд и реестр дел» не видно ни одного диалога.
/// Сообщения с грифом выше текущего допуска не выдаются (допуск могли понизить после ответа) — только их число.
/// Время — UTC (<see cref="TimeProvider"/>).
/// </remarks>
public sealed class CaseChatStore(
    IDbContextFactory<InvestigationDbContext> contextFactory,
    IAccessPolicy policy,
    IUserRoleStore roles,
    TimeProvider time) : ICaseChatStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <inheritdoc />
    public async Task<CaseChatScope?> GetScopeAsync(int caseId, AccessContext access, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(access);

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await VisibleCases(db, access, cancellationToken) is { } cases
            ? await cases.Where(c => c.Id == caseId)
                .Select(c => new CaseChatScope(c.Id, c.Number, c.Classification, c.DivisionId, c.Title))
                .FirstOrDefaultAsync(cancellationToken)
            : null;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CaseChatScope>> ListScopesAsync(AccessContext access, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(access);

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await VisibleCases(db, access, cancellationToken) is { } cases
            ? await cases
                .OrderByDescending(c => c.UpdatedAt ?? c.CreatedAt).ThenByDescending(c => c.Id)
                .Select(c => new CaseChatScope(c.Id, c.Number, c.Classification, c.DivisionId, c.Title))
                .ToListAsync(cancellationToken)
            : [];
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CaseChatSummary>?> ListAsync(int? caseId, AccessContext access, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(access);
        if (access.NumericSubjectId is not { } me)
        {
            return null;
        }

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        if (await VisibleCases(db, access, cancellationToken) is not { } cases
            || (caseId is { } id && !await cases.AnyAsync(c => c.Id == id, cancellationToken)))
        {
            return null;
        }

        var rows = await db.CaseChats.AsNoTracking()
            .Where(c => c.CaseId == caseId && c.OwnerUserId == me)
            .OrderByDescending(c => c.UpdatedAt ?? c.CreatedAt).ThenByDescending(c => c.Id)
            .Select(c => new { c.Id, c.Title, c.Classification, Updated = c.UpdatedAt ?? c.CreatedAt })
            .ToListAsync(cancellationToken);
        return rows
            .Select(r => new CaseChatSummary(r.Id, r.Title, r.Classification, DateTime.SpecifyKind(r.Updated, DateTimeKind.Utc)))
            .ToList();
    }

    /// <inheritdoc />
    public async Task<int?> CreateAsync(int? caseId, string title, AccessContext access, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(access);
        if (access.NumericSubjectId is not { } me)
        {
            return null;
        }

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        if (await VisibleCases(db, access, cancellationToken) is not { } cases)
        {
            return null;
        }

        // Диалог по делу — с грифом дела; по всем делам — без грифа, его поднимут сообщения.
        short classification = 0;
        if (caseId is { } id)
        {
            var scope = await cases.Where(c => c.Id == id).Select(c => new { c.Classification }).FirstOrDefaultAsync(cancellationToken);
            if (scope is null)
            {
                return null;
            }

            classification = scope.Classification;
        }

        var now = time.GetUtcNow().UtcDateTime;
        var chat = new CaseChat
        {
            CaseId = caseId,
            OwnerUserId = me,
            Title = TrimTitle(title),
            Classification = classification,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.CaseChats.Add(chat);
        await db.SaveChangesAsync(cancellationToken);
        return chat.Id;
    }

    /// <inheritdoc />
    public async Task<CaseChatThread?> GetAsync(int chatId, AccessContext access, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(access);

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        if (await OwnChats(db, access, cancellationToken) is not { } chats)
        {
            return null;
        }

        var chat = await chats.Where(c => c.Id == chatId)
            .Select(c => new { c.Id, c.CaseId, c.Title, c.Classification })
            .FirstOrDefaultAsync(cancellationToken);
        if (chat is null)
        {
            return null;
        }

        var clearance = access.MaxClassification;
        var messages = await db.CaseChatMessages.AsNoTracking()
            .Where(m => m.ChatId == chatId && m.Classification <= clearance)
            .OrderBy(m => m.Id)
            .Select(m => new { m.Id, m.Role, m.Text, m.Classification, m.CreatedAt, m.SourcesJson, m.CheckJson })
            .ToListAsync(cancellationToken);
        var hidden = await db.CaseChatMessages.CountAsync(m => m.ChatId == chatId && m.Classification > clearance, cancellationToken);

        return new CaseChatThread(
            chat.Id,
            chat.CaseId,
            chat.Title,
            chat.Classification,
            messages.Select(m => new CaseChatMessageRow(
                m.Id,
                m.Role,
                m.Text,
                m.Classification,
                DateTime.SpecifyKind(m.CreatedAt, DateTimeKind.Utc),
                ReadSources(m.SourcesJson),
                ReadCheck(m.CheckJson))).ToList(),
            hidden);
    }

    /// <inheritdoc />
    public async Task<bool> AppendAsync(int chatId, CaseChatMessageDraft message, AccessContext access, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(access);

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        if (await OwnChats(db, access, cancellationToken) is not { } chats)
        {
            return false;
        }

        var chat = await chats.AsTracking().FirstOrDefaultAsync(c => c.Id == chatId, cancellationToken);
        if (chat is null)
        {
            return false;
        }

        var now = time.GetUtcNow().UtcDateTime;
        var limit = message.Role == ConversationMessageRole.User ? CaseChatLimits.MaxQuestionLength : CaseChatLimits.MaxAnswerLength;
        db.CaseChatMessages.Add(new CaseChatMessage
        {
            ChatId = chatId,
            Role = message.Role,
            Text = message.Text.Length <= limit ? message.Text : message.Text[..limit] + " […ответ обрезан при сохранении]",
            Classification = message.Classification,
            SourcesJson = message.Sources is { Count: > 0 } sources ? JsonSerializer.Serialize(sources, Json) : null,
            CheckJson = message.Check is { } check ? JsonSerializer.Serialize(check, Json) : null,
            CreatedAt = now,
        });

        // Гриф диалога — максимум грифов его сообщений (наследование, ТБ-032/033); время — для порядка в списке.
        chat.Classification = Math.Max(chat.Classification, message.Classification);
        chat.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <inheritdoc />
    public async Task<bool> RenameAsync(int chatId, string title, AccessContext access, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(access);

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        if (await OwnChats(db, access, cancellationToken) is not { } chats)
        {
            return false;
        }

        var normalized = TrimTitle(title);
        return await chats.Where(c => c.Id == chatId)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.Title, normalized), cancellationToken) > 0;
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(int chatId, AccessContext access, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(access);

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        if (await OwnChats(db, access, cancellationToken) is not { } chats)
        {
            return false;
        }

        // Сообщения уходят каскадом внешнего ключа; запись журнала о вопросах и ответах остаётся (ТБ-030).
        return await chats.Where(c => c.Id == chatId).ExecuteDeleteAsync(cancellationToken) > 0;
    }

    /// <summary>Заголовок: пробелы схлопнуты, пустой — «Новый диалог», длинный — обрезан.</summary>
    public static string TrimTitle(string? title)
    {
        var normalized = string.Join(' ', (title ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (normalized.Length == 0)
        {
            return "Новый диалог";
        }

        return normalized.Length <= CaseChatLimits.MaxTitleLength ? normalized : normalized[..(CaseChatLimits.MaxTitleLength - 1)] + "…";
    }

    // Дела, видимые субъекту, по полной решётке; без права «Дашборд и реестр дел» — null (ничего не видно).
    private async Task<IQueryable<CaseFile>?> VisibleCases(InvestigationDbContext db, AccessContext access, CancellationToken cancellationToken)
    {
        var role = await PermissionRule.ResolveCaseViewerAsync(roles, access.NumericSubjectId, cancellationToken);
        return role is null ? null : CaseAccessRule.Apply(db.Cases.AsNoTracking(), access, policy, role);
    }

    // Свои диалоги видимых дел.
    private async Task<IQueryable<CaseChat>?> OwnChats(InvestigationDbContext db, AccessContext access, CancellationToken cancellationToken)
    {
        if (access.NumericSubjectId is not { } me || await VisibleCases(db, access, cancellationToken) is not { } cases)
        {
            return null;
        }

        var visible = cases.Select(c => (int?)c.Id);
        return db.CaseChats.AsNoTracking().Where(c => c.OwnerUserId == me && (c.CaseId == null || visible.Contains(c.CaseId)));
    }

    private static List<CaseChatSource> ReadSources(string? json) =>
        json is null ? [] : JsonSerializer.Deserialize<List<CaseChatSource>>(json, Json) ?? [];

    private static CaseChatCheck? ReadCheck(string? json) =>
        json is null ? null : JsonSerializer.Deserialize<CaseChatCheck>(json, Json);
}
