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
/// <remarks>
/// ПОЛНОТА (ЦС-02, ТО-мат-08, ADR-0019 v0.2): обход HNSW отдаёт <c>ef_search</c> ближайших по ВСЕЙ таблице, а решётку,
/// область, актуальность и пригодность применяет уже к ним. Если ближе к пробе больше <c>ef_search</c> шаблонов вне
/// области — кадры того же человека в закрытом деле, в деле ОТМ или чужого подразделения, — индекс отдаёт только их, и
/// после отсева выдача пуста, хотя совпадение в области есть (проба 02.11.2026: 0 из 3). Поэтому, пока
/// шаблонов-кандидатов не больше предела точного перебора, поиск идёт ТОЧНЫМ перебором; выше предела — HNSW с
/// итеративным обходом pgvector 0.8+, который продолжает обход, пока не наберёт topK строк, прошедших фильтр.
/// </remarks>
/// <param name="contextFactory">Фабрика контекста (на операцию, ТС-008).</param>
/// <param name="accessPolicy">Сужающая политика профиля (только поверх floor'а ядра, ADR-0014).</param>
/// <param name="hnswEfSearch">Широта обхода HNSW-графа (<c>hnsw.ef_search</c>), по умолчанию 200.</param>
/// <param name="exactScanLimit">
/// До скольких шаблонов-кандидатов поиск идёт точным перебором (<c>Media:Search:ExactScanLimit</c>), по умолчанию —
/// <see cref="DefaultExactScanLimit"/>; <c>0</c> — только индекс.
/// </param>
public sealed class PgVectorFaceSearch(
    IDbContextFactory<MediaDbContext> contextFactory,
    IAccessPolicy accessPolicy,
    int hnswEfSearch = PgVectorFaceSearch.DefaultEfSearch,
    int exactScanLimit = PgVectorFaceSearch.DefaultExactScanLimit) : IFaceSearch
{
    /// <summary>
    /// Широта обхода HNSW по умолчанию. Дефолт pgvector (40) доказанно пропускает малые «острова»
    /// графа (инцидент 26.08.2026 на core.embedding); 200 — проверенное значение в паре с
    /// ef_construction=512 (см. docs/reference/pgvector-hnsw-recall.md).
    /// </summary>
    public const int DefaultEfSearch = 200;

    /// <summary>
    /// Предел точного перебора по умолчанию — 1 000 000 шаблонов, объём эталонной базы экземпляра по ТН-005. Проба
    /// 02.11.2026: перебор 1 млн шаблонов — 0,1–0,25 с, в пределах ТН-006 и для поиска по десяти эталонам (ADR-0019 v0.2).
    /// </summary>
    public const int DefaultExactScanLimit = 1_000_000;

    /// <summary>
    /// Сколько строк итеративный обход HNSW просматривает самое большее (<c>hnsw.max_scan_tuples</c>, у pgvector — 20 000):
    /// столько кадров одного человека вне области может оказаться ближе совпадения в ней.
    /// </summary>
    public const int IterativeScanMaxTuples = 100_000;

    /// <summary>
    /// Память итеративного обхода, кратно <c>work_mem</c> (<c>hnsw.scan_mem_multiplier</c>): при 1 и <c>work_mem</c> 4 МБ
    /// обход обрывается около 20 тыс. строк — раньше <see cref="IterativeScanMaxTuples"/> (проба 02.11.2026).
    /// </summary>
    public const int IterativeScanMemoryMultiplier = 8;

    // Верхняя граница предела: подсчёт «предел + 1» не должен переполниться.
    private const int MaxExactScanLimit = 100_000_000;

    private const string TemplateTable = MediaDbContext.Schema + ".face_template";

    // Итеративный обход HNSW появился в pgvector 0.8.0; у более старых версий параметров hnsw.iterative_scan нет.
    private static readonly Version IterativeScanVersion = new(0, 8, 0);

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

        // ПОЛНОТА (см. remarks): пока кандидатов не больше предела — точный перебор, обход индексом в этой транзакции
        // запрещён (как в ядре, ADR-0051 п. 6); B-tree для отбора по решётке и области работают битовыми картами.
        // Выше предела — итеративный обход HNSW, если pgvector на сервере его умеет; иначе — прежний обход.
        if (await FitsExactScanAsync(db, candidates, cancellationToken))
        {
            await db.Database.ExecuteSqlAsync(
                $"SELECT set_config('enable_indexscan', 'off', true)", cancellationToken);
        }
        else if (await SupportsIterativeScanAsync(db, cancellationToken))
        {
            var maxTuples = IterativeScanMaxTuples.ToString(CultureInfo.InvariantCulture);
            var memoryMultiplier = IterativeScanMemoryMultiplier.ToString(CultureInfo.InvariantCulture);
            await db.Database.ExecuteSqlAsync(
                $"""
                SELECT set_config('hnsw.iterative_scan', 'strict_order', true),
                       set_config('hnsw.max_scan_tuples', {maxTuples}, true),
                       set_config('hnsw.scan_mem_multiplier', {memoryMultiplier}, true)
                """,
                cancellationToken);
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

    /// <summary>
    /// Помещается ли поиск в точный перебор (ADR-0019 v0.2): таблица шаблонов не больше предела по оценке каталога, а
    /// если больше или ещё не анализировалась — кандидатов с фильтрами запроса не больше предела. Подсчёт обрывается на
    /// «предел + 1», поэтому на большой таблице не дороже самого перебора.
    /// </summary>
    private async Task<bool> FitsExactScanAsync(
        MediaDbContext db, IQueryable<FaceTemplate> candidates, CancellationToken cancellationToken)
    {
        var limit = Math.Clamp(exactScanLimit, 0, MaxExactScanLimit);
        if (limit == 0)
        {
            return false;
        }

        // Оценка числа строк из pg_class — без чтения таблицы; −1 — таблица ещё не анализировалась.
        var tableRows = await db.Database
            .SqlQuery<long>($"""SELECT reltuples::bigint AS "Value" FROM pg_class WHERE oid = to_regclass({TemplateTable})""")
            .SingleOrDefaultAsync(cancellationToken);
        if (tableRows >= 0 && tableRows <= limit)
        {
            return true;
        }

        return await candidates.Take(limit + 1).CountAsync(cancellationToken) <= limit;
    }

    /// <summary>Умеет ли pgvector на сервере итеративный обход HNSW (0.8.0+): версия расширения — из каталога.</summary>
    private static async Task<bool> SupportsIterativeScanAsync(MediaDbContext db, CancellationToken cancellationToken)
    {
        var version = await db.Database
            .SqlQuery<string>($"""SELECT extversion AS "Value" FROM pg_extension WHERE extname = 'vector'""")
            .SingleOrDefaultAsync(cancellationToken);
        return Version.TryParse(version, out var parsed) && parsed >= IterativeScanVersion;
    }
}
