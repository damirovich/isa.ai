using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ISC.AI.Abstractions.Corpus;
using ISC.AI.Abstractions.Security;
using ISC.AI.Persistence;
using ISC.AI.Profile.Inspector.Domain.Entities;
using ISC.AI.Profile.Inspector.Domain.Enums;
using ISC.AI.Profile.Inspector.Domain.Services;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ISC.AI.Profile.Inspector.Data;

/// <summary>
/// Картотека НПА (<see cref="INormRegistryStore"/>, ТФ-НПА-02) над двумя схемами одной БД:
/// нормы/редакции/связки — <c>inspector</c>, корпус — <c>core</c>. Через границу схем — только
/// слабые ссылки по значению и раздельные контексты (ТО-инф-06): join двух контекстов делается
/// в памяти по спискам идентификаторов, а не межсхемным SQL.
/// </summary>
/// <remarks>
/// Режимные решения (закреплены ревью 2026-08-10):
/// <list type="number">
/// <item>Привязка НИКОГДА не поднимает видимость чанков — только гасит (привязка к утратившей силу
/// редакции скрывает текст немедленно, hide-first). Поднятие — только явной сменой статуса
/// редакции через материализатор с его ограждениями.</item>
/// <item>Реквизиты документов корпуса (заголовок, тип, гриф) выдаются ТОЛЬКО в пределах решётки
/// допуска субъекта (ТБ-020/021): сам факт связки «норма ↔ документ №N» не грифован (живёт в
/// профиле), а вот заголовок документа ДСП — уже содержание.</item>
/// <item>Дубли связок исключены уникальными индексами БД, а не только предварительной проверкой:
/// два оператора (или двойной клик) не должны задваивать данные, которые нечем чинить.</item>
/// </list>
/// </remarks>
public sealed class NormRegistryStore(
    IDbContextFactory<InspectorDbContext> inspectorFactory,
    IDbContextFactory<CoreDbContext> coreFactory,
    IChunkCurrencyPort chunkCurrencyPort) : INormRegistryStore
{
    /// <summary>Предел размера страницы — тот же, что у реестров документооборота.</summary>
    public const int MaxPageSize = 200;

    /// <inheritdoc />
    public async Task<NormRegistryPage> ListAsync(
        NormListFilter filter, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);

        await using var db = await inspectorFactory.CreateDbContextAsync(cancellationToken);

        var query = db.LegalNorms.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(filter.Text))
        {
            var pattern = LikeContains(filter.Text);
            query = query.Where(n =>
                EF.Functions.ILike(n.Identifier, pattern) || EF.Functions.ILike(n.Title, pattern));
        }

        if (filter.Status is { } status)
        {
            // Отбор по статусу «ведущей» редакции: действующая норма — есть Active-редакция;
            // утратившая — редакции есть и ни одной действующей. Нормы без редакций не подходят
            // ни под один статус — они видны только без фильтра.
            query = status == RevisionStatus.Active
                ? query.Where(n => n.Revisions.Any(r => r.Status == RevisionStatus.Active))
                : query.Where(n => n.Revisions.Any() && n.Revisions.All(r => r.Status != RevisionStatus.Active));
        }

        // Общее число — ДО среза страницы: навигация должна знать, сколько всего подошло.
        var total = await query.CountAsync(cancellationToken);

        var page = Math.Max(1, filter.Page);
        var pageSize = Math.Clamp(filter.PageSize, 1, MaxPageSize);

        // Проекция в анонимный тип: конструктор record внутри проекций EF переводит не всегда
        // (тот же обход, что в источнике дашборда) — материализуем и собираем record в памяти.
        var rows = await query
            .OrderByDescending(n => n.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(n => new
            {
                n.Id,
                n.Identifier,
                n.Title,
                RevisionCount = n.Revisions.Count,
                HasActive = n.Revisions.Any(r => r.Status == RevisionStatus.Active),
                LatestEffectiveDate = n.Revisions
                    .OrderByDescending(r => r.EffectiveDate)
                    .Select(r => (DateOnly?)r.EffectiveDate)
                    .FirstOrDefault(),
            })
            .ToListAsync(cancellationToken);

        var normIds = rows.Select(r => r.Id).ToList();
        var linkCounts = await db.NormDocumentLinks.AsNoTracking()
            .Where(l => normIds.Contains(l.LegalNormId))
            .GroupBy(l => l.LegalNormId)
            .Select(g => new { NormId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.NormId, g => g.Count, cancellationToken);

        var items = rows
            .Select(r => new NormListItem(
                r.Id,
                r.Identifier,
                r.Title,
                r.RevisionCount,
                r.RevisionCount == 0 ? null : r.HasActive ? RevisionStatus.Active : RevisionStatus.Repealed,
                r.LatestEffectiveDate,
                linkCounts.GetValueOrDefault(r.Id)))
            .ToList();

        return new NormRegistryPage(items, total);
    }

    /// <inheritdoc />
    public async Task<NormDetails?> GetAsync(
        int normId, AccessContext access, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(access);

        await using var db = await inspectorFactory.CreateDbContextAsync(cancellationToken);

        var norm = await db.LegalNorms.AsNoTracking()
            .Where(n => n.Id == normId)
            .Select(n => new { n.Id, n.Identifier, n.Title })
            .FirstOrDefaultAsync(cancellationToken);
        if (norm is null)
        {
            return null;
        }

        var revisions = await db.NormRevisions.AsNoTracking()
            .Where(r => r.NormId == normId)
            .OrderByDescending(r => r.EffectiveDate).ThenByDescending(r => r.Id)
            .Select(r => new
            {
                r.Id,
                r.Status,
                r.EffectiveDate,
                r.RepealedDate,
                ChunkCount = db.ChunkRevisionLinks.Count(l => l.NormRevisionId == r.Id),
            })
            .ToListAsync(cancellationToken);

        var documentIds = await db.NormDocumentLinks.AsNoTracking()
            .Where(l => l.LegalNormId == normId)
            .Select(l => l.DocumentId)
            .ToListAsync(cancellationToken);

        // Реквизиты — из корпуса вторым контекстом, И ТОЛЬКО в пределах решётки допуска (ТБ-020/021):
        // заголовок документа ДСП — содержание, субъекту вне допуска отдаётся лишь «документ №N».
        // Документа может уже не быть (гарантированное удаление, ТБ-064) — связка «висячая».
        var documents = new List<LinkedCorpusDocument>(documentIds.Count);
        if (documentIds.Count > 0)
        {
            await using var core = await coreFactory.CreateDbContextAsync(cancellationToken);
            var allowedDivisions = access.AllowedDivisions;
            var alive = await core.Documents.AsNoTracking()
                .Where(d => documentIds.Contains(d.Id))
                .Select(d => new
                {
                    d.Id,
                    d.Title,
                    d.DocType,
                    d.DocDate,
                    d.Classification,
                    Visible = d.Classification <= access.MaxClassification
                        && allowedDivisions.Contains(d.DivisionId),
                })
                .ToDictionaryAsync(d => d.Id, cancellationToken);

            documents.AddRange(documentIds
                .OrderByDescending(id => id)
                .Select(id => alive.TryGetValue(id, out var d)
                    ? d.Visible
                        ? new LinkedCorpusDocument(id, d.Title, d.DocType, d.DocDate, d.Classification, IsAlive: true)
                        // Вне допуска: связка видна (номер — не содержание), реквизиты — нет.
                        : new LinkedCorpusDocument(id, null, null, null, null, IsAlive: true)
                    : new LinkedCorpusDocument(id, null, null, null, null, IsAlive: false)));
        }

        return new NormDetails(
            norm.Id,
            norm.Identifier,
            norm.Title,
            [.. revisions.Select(r => new NormRevisionItem(r.Id, r.Status, r.EffectiveDate, r.RepealedDate, r.ChunkCount))],
            documents);
    }

    /// <inheritdoc />
    public async Task<(NormWriteResult Result, int NormId)> CreateAsync(
        string identifier, string title, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identifier);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);

        await using var db = await inspectorFactory.CreateDbContextAsync(cancellationToken);

        var normalized = identifier.Trim();
        if (await db.LegalNorms.AnyAsync(n => n.Identifier == normalized, cancellationToken))
        {
            return (NormWriteResult.DuplicateIdentifier, 0);
        }

        var norm = new LegalNorm { Identifier = normalized, Title = title.Trim() };
        db.LegalNorms.Add(norm);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception))
        {
            // Гонка двух операторов: проверка выше прошла у обоих, уникальный индекс поймал второго.
            return (NormWriteResult.DuplicateIdentifier, 0);
        }

        return (NormWriteResult.Ok, norm.Id);
    }

    /// <inheritdoc />
    public async Task<NormWriteResult> UpdateAsync(
        int normId, string identifier, string title, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identifier);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);

        await using var db = await inspectorFactory.CreateDbContextAsync(cancellationToken);

        var norm = await db.LegalNorms.FirstOrDefaultAsync(n => n.Id == normId, cancellationToken);
        if (norm is null)
        {
            return NormWriteResult.NotFound;
        }

        var normalized = identifier.Trim();
        if (await db.LegalNorms.AnyAsync(n => n.Id != normId && n.Identifier == normalized, cancellationToken))
        {
            return NormWriteResult.DuplicateIdentifier;
        }

        norm.Identifier = normalized;
        norm.Title = title.Trim();
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception))
        {
            return NormWriteResult.DuplicateIdentifier;
        }

        return NormWriteResult.Ok;
    }

    /// <inheritdoc />
    public async Task<(NormWriteResult Result, int RevisionId)> AddRevisionAsync(
        int normId, DateOnly effectiveDate, CancellationToken cancellationToken = default)
    {
        await using var db = await inspectorFactory.CreateDbContextAsync(cancellationToken);

        if (!await db.LegalNorms.AnyAsync(n => n.Id == normId, cancellationToken))
        {
            return (NormWriteResult.NotFound, 0);
        }

        var revision = new NormRevision
        {
            NormId = normId,
            Status = RevisionStatus.Active,
            EffectiveDate = effectiveDate,
        };
        db.NormRevisions.Add(revision);
        await db.SaveChangesAsync(cancellationToken);

        return (NormWriteResult.Ok, revision.Id);
    }

    /// <inheritdoc />
    public async Task<(NormWriteResult Result, int LinkedChunks)> LinkDocumentAsync(
        int normId, int revisionId, int coreDocumentId, CancellationToken cancellationToken = default)
    {
        await using var db = await inspectorFactory.CreateDbContextAsync(cancellationToken);

        // Редакция обязана принадлежать этой норме: связка чужой редакции тихо перепутала бы
        // видимость чанков другой нормы. Статус нужен ниже — привязка обязана его материализовать.
        var revision = await db.NormRevisions.AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == revisionId && r.NormId == normId, cancellationToken);
        if (revision is null)
        {
            return (NormWriteResult.NotFound, 0);
        }

        if (await db.NormDocumentLinks.AsNoTracking()
                .AnyAsync(l => l.LegalNormId == normId && l.DocumentId == coreDocumentId, cancellationToken))
        {
            return (NormWriteResult.AlreadyLinked, 0);
        }

        // Чанки — из корпуса вторым контекстом. Погашенный заменой документ (supersede) не привязывается:
        // его текст корпус уже заменил; документ без чанков — тоже отказ, мёртвая связка без единого
        // фрагмента ничего не даёт, а отвязки в картотеке нет.
        List<int> chunkIds;
        await using (var core = await coreFactory.CreateDbContextAsync(cancellationToken))
        {
            var document = await core.Documents.AsNoTracking()
                .Where(d => d.Id == coreDocumentId)
                .Select(d => new { d.SupersededByDocumentId })
                .FirstOrDefaultAsync(cancellationToken);
            if (document is null || document.SupersededByDocumentId is not null)
            {
                return (NormWriteResult.NotFound, 0);
            }

            chunkIds = await core.Chunks.AsNoTracking()
                .Where(c => c.DocumentId == coreDocumentId)
                .Select(c => c.Id)
                .ToListAsync(cancellationToken);
        }

        if (chunkIds.Count == 0)
        {
            return (NormWriteResult.NotFound, 0);
        }

        // Привязка к УТРАТИВШЕЙ СИЛУ редакции гасит чанки СРАЗУ и ДО записи связок (hide-first,
        // тот же режимно-безопасный порядок, что у материализатора): привязали старый текст к старой
        // редакции — он не должен ни секунды выдаваться как действующий (GATE-3). Обратного нет:
        // привязка к действующей редакции видимость НЕ поднимает — чанки могли быть погашены заменой
        // или другой нормой, и поднимает их только явная смена статуса с её ограждениями.
        if (revision.Status != RevisionStatus.Active)
        {
            await chunkCurrencyPort.SetCurrencyAsync(chunkIds, isCurrent: false, cancellationToken);
        }

        db.NormDocumentLinks.Add(new NormDocumentLink { LegalNormId = normId, DocumentId = coreDocumentId });
        foreach (var chunkId in chunkIds)
        {
            db.ChunkRevisionLinks.Add(new ChunkRevisionLink { NormRevisionId = revisionId, ChunkId = chunkId });
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception))
        {
            // Гонка (двойной клик, два оператора): проверка выше прошла у обоих, индекс поймал второго.
            return (NormWriteResult.AlreadyLinked, 0);
        }

        return (NormWriteResult.Ok, chunkIds.Count);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CorpusDocumentCandidate>> SearchCorpusDocumentsAsync(
        int normId, string? text, AccessContext access, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(access);

        await using var db = await inspectorFactory.CreateDbContextAsync(cancellationToken);
        var linkedIds = await db.NormDocumentLinks.AsNoTracking()
            .Where(l => l.LegalNormId == normId)
            .Select(l => l.DocumentId)
            .ToListAsync(cancellationToken);

        await using var core = await coreFactory.CreateDbContextAsync(cancellationToken);
        var allowedDivisions = access.AllowedDivisions;
        var query = core.Documents.AsNoTracking()
            // Решётка допуска В ЗАПРОСЕ (ТБ-020/021): кандидаты с их заголовками и грифами — только
            // из числа документов, которые субъект и так вправе видеть; fail-closed по построению.
            .Where(d => d.Classification <= access.MaxClassification
                && allowedDivisions.Contains(d.DivisionId))
            // Погашенные заменой не предлагаются (текст уже заменён), пустые — тоже (привязка
            // без единого фрагмента ничего не даёт и всё равно будет отклонена).
            .Where(d => d.SupersededByDocumentId == null && d.Chunks.Any())
            .Where(d => !linkedIds.Contains(d.Id));

        if (!string.IsNullOrWhiteSpace(text))
        {
            var pattern = LikeContains(text);
            query = query.Where(d => EF.Functions.ILike(d.Title, pattern) || EF.Functions.ILike(d.DocType, pattern));
        }

        var rows = await query
            .OrderByDescending(d => d.Id)
            .Take(50)
            .Select(d => new { d.Id, d.Title, d.DocType, d.DocDate, d.Classification })
            .ToListAsync(cancellationToken);

        return [.. rows.Select(d => new CorpusDocumentCandidate(d.Id, d.Title, d.DocType, d.DocDate, d.Classification))];
    }

    /// <summary>
    /// Шаблон «содержит» для ILIKE с экранированием спецсимволов маски: «%», «_» и «\» в пользовательском
    /// тексте — буквальные символы поиска, а не маски (номер вида «50_1%» обязан искаться дословно).
    /// </summary>
    private static string LikeContains(string text)
    {
        var escaped = text.Trim()
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);
        return $"%{escaped}%";
    }

    // Только нарушение уникальности (23505) считается дублем; прочие сбои БД — наружу, а не ложная
    // диагностика «такой номер уже есть» при оборванном соединении.
    private static bool IsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };
}
