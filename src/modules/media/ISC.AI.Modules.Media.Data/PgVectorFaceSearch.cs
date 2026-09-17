using System.Globalization;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.Media.Data.Entities;
using ISC.AI.Modules.Media.Domain.Model;
using ISC.AI.Modules.Media.Domain.Services;
using Microsoft.EntityFrameworkCore;
using Pgvector;
using Pgvector.EntityFrameworkCore;

namespace ISC.AI.Modules.Media.Data;

/// <summary>
/// Поиск ближайших шаблонов лиц в pgvector (ТС-012) с решёткой доступа НА СТОРОНЕ БД (ТБ-020/070,
/// GATE-4). Повторяет схему ретривера ядра (<c>PgVectorRetriever</c>, GATE-1): floor ядра → сужение
/// профиля → актуальность → косинусное расстояние → порог → topK — всё одним SQL-запросом.
/// </summary>
/// <param name="contextFactory">Фабрика контекста (на операцию, ТС-008).</param>
/// <param name="accessPolicy">Сужающая политика профиля (только поверх floor'а ядра, ADR-0014).</param>
/// <param name="hnswEfSearch">Широта обхода HNSW-графа (<c>hnsw.ef_search</c>), по умолчанию 200.</param>
public sealed class PgVectorFaceSearch(
    IDbContextFactory<MediaDbContext> contextFactory,
    IAccessPolicy accessPolicy,
    int hnswEfSearch = PgVectorFaceSearch.DefaultEfSearch) : IFaceSearch
{
    /// <summary>
    /// Широта обхода HNSW по умолчанию. Дефолт pgvector (40) доказанно пропускает малые «острова»
    /// графа (инцидент 26.08.2026 на core.embedding); 200 — проверенное значение в паре с
    /// ef_construction=512 (см. docs/reference/pgvector-hnsw-recall.md).
    /// </summary>
    public const int DefaultEfSearch = 200;

    /// <inheritdoc />
    public async Task<IReadOnlyList<FaceCandidate>> SearchAsync(
        FaceSearchQuery query, AccessContext access, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        // fail-closed: без субъекта с допуском поиск невозможен (ТБ-012/021) — исключение, не «без фильтра».
        if (access is null)
        {
            throw new AccessContextRequiredException();
        }

        if (query.Probe.Length != FaceTemplate.Dimensions)
        {
            throw new ArgumentException(
                $"Размерность пробы {query.Probe.Length} не совпадает с размерностью шаблонов {FaceTemplate.Dimensions} (ADR-0020).",
                nameof(query));
        }

        var probe = new Vector(query.Probe);
        var topK = Math.Clamp(query.TopK, 1, 500);

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        // Широта обхода HNSW — на каждую поисковую транзакцию (ТБ-022). set_config(..., is_local: true)
        // ≡ SET LOCAL, поэтому нужна явная транзакция; без индекса настройка безвредна.
        var efSearch = Math.Clamp(hnswEfSearch, 10, 1000).ToString(CultureInfo.InvariantCulture);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.Database.ExecuteSqlAsync(
            $"SELECT set_config('hnsw.ef_search', {efSearch}, true)", cancellationToken);

        // PRE-FILTER (ТБ-020): floor ядра (гриф ≤ допуск ∧ подразделение ∈ разрешённых) + сужение профиля.
        // Шаблоны вне допуска не участвуют даже в ранжировании — фильтр стоит ДО оператора расстояния.
        IQueryable<FaceTemplate> candidates = db.Templates
            .Where(BaselineAccess.Filter<FaceTemplate>(access))
            .Where(accessPolicy.BuildFilter<FaceTemplate>(access));

        // По умолчанию — только актуальные носители (ADR-0013) и только пригодные лица (ТО-мат-07).
        if (!query.IncludeStale)
        {
            candidates = candidates.Where(t => t.IsCurrent);
        }

        candidates = candidates.Where(t => t.QualityAcceptable);

        // ОБЛАСТЬ ПОИСКА (ТБ-071, ТФ-ПЛ-05): носители дел, доступных субъекту — ПОСЛЕ решётки, а не вместо
        // неё: шаблон в области, но вне допуска, всё равно не выдаётся. Пустая коллекция — пустая выдача
        // (у субъекта нет ни одного носителя в области), а не «все носители»: null и [] здесь различаются.
        if (query.AssetIds is { } assetIds)
        {
            var ids = assetIds as int[] ?? assetIds.ToArray();
            candidates = ids.Length == 0
                ? candidates.Where(t => false)
                : candidates.Where(t => ids.Contains(t.AssetId));
        }

        var scored = candidates.Select(t => new { Template = t, Distance = t.Embedding.CosineDistance(probe) });

        // Порог — ПОСЛЕ фильтра доступа, не вместо него (ТО-мат-05): сужает выдачу по качеству совпадения.
        if (query.MaxCosineDistance is { } maxDistance)
        {
            scored = scored.Where(s => s.Distance <= maxDistance);
        }

        var results = await scored
            .OrderBy(s => s.Distance)
            .Take(topK)
            .Select(s => new FaceCandidate(
                s.Template.FaceId,
                s.Template.AssetId,
                s.Template.Face!.Frame != null ? s.Template.Face.Frame.Index : (int?)null,
                s.Template.Face.Frame != null ? s.Template.Face.Frame.TimestampMs : (long?)null,
                s.Distance,
                s.Template.Classification,
                s.Template.DivisionId,
                s.Template.ModelVersion))
            .ToListAsync(cancellationToken);

        // Транзакция только скоупит SET LOCAL — изменений данных нет.
        await transaction.CommitAsync(cancellationToken);
        return results;
    }
}
