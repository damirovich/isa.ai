using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ISC.AI.Abstractions.Ingestion;
using ISC.AI.Abstractions.Security;
using ISC.AI.Persistence;
using ISC.AI.Persistence.Entities;
using ISC.AI.Profile.Inspector.Domain.Services;
using Microsoft.EntityFrameworkCore;

namespace ISC.AI.Profile.Inspector.Data;

/// <summary>
/// Каталог корпуса НПА (<see cref="ICorpusCatalog"/>, ТФ-НПА-01/02) над схемой <c>core</c>
/// (разрешено: <c>Inspector.Data</c> — единственное место профиля со ссылкой на Persistence ядра)
/// плюс связки картотеки из схемы <c>inspector</c> — вторым контекстом, по значениям (ТО-инф-06).
/// </summary>
public sealed class CorpusCatalog(
    IDbContextFactory<CoreDbContext> coreFactory,
    IDbContextFactory<InspectorDbContext> inspectorFactory) : ICorpusCatalog
{
    /// <summary>Предел размера страницы — тот же, что у остальных реестров.</summary>
    public const int MaxPageSize = 200;

    /// <inheritdoc />
    public async Task<CorpusCatalogPage> ListAsync(
        CorpusCatalogFilter filter, AccessContext access, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(access);

        await using var core = await coreFactory.CreateDbContextAsync(cancellationToken);

        var visible = VisibleDocuments(core, access);

        // Виды актов — из ВИДИМОЙ части корпуса (не подсказывать типы, которых субъект не увидит),
        // до остальных фильтров — иначе выбранный тип выпадал бы из списка после применения.
        var docTypes = await visible
            .Select(d => d.DocType)
            .Distinct()
            .OrderBy(t => t)
            .ToListAsync(cancellationToken);

        var query = visible;

        if (!string.IsNullOrWhiteSpace(filter.Text))
        {
            var pattern = LikeContains(filter.Text);
            query = query.Where(d => EF.Functions.ILike(d.Title, pattern)
                || (d.Source != null && EF.Functions.ILike(d.Source, pattern)));
        }

        if (!string.IsNullOrWhiteSpace(filter.DocType))
        {
            query = query.Where(d => d.DocType == filter.DocType);
        }

        // Статус действия — ОДНА семантика и в фильтре, и в проекции (см. Currency ниже):
        // заменённый новой версией — утратил силу независимо от чанков; без чанков — «нет текста»;
        // иначе по чанкам: все действуют / все погашены / часть. Ревью нашло расхождение фильтра
        // с отображением (документ без чанков показывался «действующим», но под фильтром пропадал).
        if (filter.Currency is { } currency)
        {
            query = currency switch
            {
                CorpusDocumentCurrency.NoText => query.Where(d => d.SupersededByDocumentId == null && !d.Chunks.Any()),
                CorpusDocumentCurrency.Current => query.Where(d => d.SupersededByDocumentId == null
                    && d.Chunks.Any() && d.Chunks.All(c => c.IsCurrent)),
                CorpusDocumentCurrency.Superseded => query.Where(d => d.SupersededByDocumentId != null
                    || (d.Chunks.Any() && d.Chunks.All(c => !c.IsCurrent))),
                _ => query.Where(d => d.SupersededByDocumentId == null
                    && d.Chunks.Any(c => c.IsCurrent) && d.Chunks.Any(c => !c.IsCurrent)),
            };
        }

        // Общее число — ДО среза страницы.
        var total = await query.CountAsync(cancellationToken);

        query = filter.Sort switch
        {
            // Без даты — в конце при любом направлении: документ без даты не «новее» и не «старее».
            CorpusCatalogSort.DateAsc => query.OrderBy(d => d.DocDate == null).ThenBy(d => d.DocDate).ThenBy(d => d.Id),
            CorpusCatalogSort.TitleAsc => query.OrderBy(d => d.Title).ThenBy(d => d.Id),
            CorpusCatalogSort.LoadedDesc => query.OrderByDescending(d => d.CreatedAt).ThenByDescending(d => d.Id),
            _ => query.OrderBy(d => d.DocDate == null).ThenByDescending(d => d.DocDate).ThenByDescending(d => d.Id),
        };

        var page = Math.Max(1, filter.Page);
        var pageSize = Math.Clamp(filter.PageSize, 1, MaxPageSize);

        // Условный Count в проекции EF не переводится — Sum(cond ? 1 : 0) (обход из источника дашборда).
        var rows = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(d => new
            {
                d.Id,
                d.Title,
                d.DocType,
                d.DocDate,
                d.Classification,
                d.CreatedAt,
                d.SupersededByDocumentId,
                ChunkCount = d.Chunks.Count,
                CurrentCount = d.Chunks.Sum(c => c.IsCurrent ? 1 : 0),
            })
            .ToListAsync(cancellationToken);

        var items = rows
            .Select(r => new CorpusCatalogItem(
                r.Id, r.Title, r.DocType, r.DocDate, r.Classification,
                Currency(r.SupersededByDocumentId, r.ChunkCount, r.CurrentCount), r.ChunkCount, r.CreatedAt))
            .ToList();

        return new CorpusCatalogPage(items, total, docTypes);
    }

    /// <inheritdoc />
    public async Task<CorpusDocumentDetails?> GetAsync(
        int documentId, AccessContext access, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(access);

        await using var core = await coreFactory.CreateDbContextAsync(cancellationToken);

        // Допуск — в самом запросе: документ вне допуска даёт null, неотличимый от «не найден».
        var document = await VisibleDocuments(core, access)
            .Where(d => d.Id == documentId)
            .Select(d => new
            {
                d.Id,
                d.Title,
                d.DocType,
                d.Source,
                d.DocDate,
                d.Classification,
                d.DivisionId,
                d.SupersededByDocumentId,
                d.CreatedAt,
            })
            .FirstOrDefaultAsync(cancellationToken);
        if (document is null)
        {
            return null;
        }

        var fragmentRows = await core.Chunks.AsNoTracking()
            .Where(c => c.DocumentId == documentId)
            .OrderBy(c => c.Ordinal)
            .Select(c => new { c.Ordinal, c.Text, c.IsCurrent })
            .ToListAsync(cancellationToken);
        var fragments = fragmentRows
            .Select(f => new CorpusDocumentFragment(f.Ordinal, f.Text, f.IsCurrent))
            .ToList();

        // Преемник показывается ТОЛЬКО если он сам в пределах допуска субъекта: иначе ссылка
        // «заменён документом №N» выдавала бы существование недоступного документа (ТБ-020/021 —
        // недоступное неотличимо от несуществующего). Статус «утратил силу» при этом остаётся.
        int? visibleSuccessorId = null;
        if (document.SupersededByDocumentId is { } successorId
            && await VisibleDocuments(core, access).AnyAsync(d => d.Id == successorId, cancellationToken))
        {
            visibleSuccessorId = successorId;
        }

        // Нормы картотеки, к которым привязан документ, — из схемы inspector по значению DocumentId.
        await using var inspector = await inspectorFactory.CreateDbContextAsync(cancellationToken);
        var normRows = await inspector.NormDocumentLinks.AsNoTracking()
            .Where(l => l.DocumentId == documentId)
            .Select(l => new { l.LegalNorm!.Id, l.LegalNorm.Identifier, l.LegalNorm.Title })
            .ToListAsync(cancellationToken);
        var linkedNorms = normRows
            .Select(n => new CorpusLinkedNorm(n.Id, n.Identifier, n.Title))
            .ToList();

        return new CorpusDocumentDetails(
            document.Id, document.Title, document.DocType, document.Source, document.DocDate,
            document.Classification, document.DivisionId,
            Currency(document.SupersededByDocumentId, fragments.Count, fragments.Count(f => f.IsCurrent)),
            visibleSuccessorId, document.CreatedAt, fragments, linkedNorms);
    }

    /// <summary>
    /// Документы корпуса, видимые субъекту: решётка «гриф ≤ допуска И подразделение ∈ разрешённых»
    /// (ТБ-020/021, fail-closed — пустой список подразделений даёт пусто) — тот же предикат, что
    /// у ретривера (BaselineAccess) и документооборота; минус документооборот (у него свой реестр).
    /// </summary>
    private static IQueryable<DocumentEntity> VisibleDocuments(CoreDbContext core, AccessContext access)
    {
        var allowedDivisions = access.AllowedDivisions;
        return core.Documents.AsNoTracking()
            .Where(d => d.Classification <= access.MaxClassification
                && allowedDivisions.Contains(d.DivisionId))
            .Where(d => d.Source != CorpusSources.DocFlow);
    }

    // Та же семантика, что у SQL-фильтра в ListAsync — менять только парно.
    private static CorpusDocumentCurrency Currency(int? supersededBy, int chunkCount, int currentCount) =>
        supersededBy is not null ? CorpusDocumentCurrency.Superseded
        : chunkCount == 0 ? CorpusDocumentCurrency.NoText
        : currentCount == chunkCount ? CorpusDocumentCurrency.Current
        : currentCount == 0 ? CorpusDocumentCurrency.Superseded
        : CorpusDocumentCurrency.Mixed;

    /// <summary>Шаблон «содержит» для ILIKE с экранированием масок (тот же приём, что в картотеке).</summary>
    private static string LikeContains(string text)
    {
        var escaped = text.Trim()
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);
        return $"%{escaped}%";
    }
}
