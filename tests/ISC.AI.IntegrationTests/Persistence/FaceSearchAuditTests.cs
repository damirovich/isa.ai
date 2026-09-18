using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.Media.Data;
using ISC.AI.Modules.Media.Data.Entities;
using ISC.AI.Modules.Media.Domain.Model;
using ISC.AI.Persistence.Audit;
using Microsoft.EntityFrameworkCore;
using Pgvector;
using Shouldly;
using Testcontainers.PostgreSql;
using Xunit;

namespace ISC.AI.IntegrationTests.Persistence;

/// <summary>
/// ТБ-072 и ТБ-074 на реальной БД (ревью ЭС3, находка 31) — та часть, которую тестовый проект достаёт
/// через свои ссылки (слой данных пакета «Медиа» и журнал ядра). Проверяется, что (а) путь поиска, который
/// исполняет сценарий <c>SearchByFaceQuery</c> — <see cref="PgVectorFaceSearch"/> по вектору пробы и запись
/// сессии с кандидат-листом в <see cref="SearchSessionStore"/> — не добавляет строк ни в
/// <c>media.face_template</c>, ни в <c>media.face</c>, а сессия хранит только хеш пробы; (б) на уровне схемы
/// вектору пробы НЕКУДА осесть: единственная таблица схемы <c>media</c> со столбцом <c>vector</c> —
/// <c>face_template</c> (ТБ-074); (в) настоящий <see cref="AuditWriter"/> принимает запись поиска с копией
/// пробы предельного размера (4 МБ в base64) целиком, а <see cref="AuditReader"/> отдаёт её только субъекту
/// с допуском не ниже грифа дела и в подразделении дела (ТБ-072/032).
/// </summary>
/// <remarks>
/// Требуется Docker. Сквозной вариант этих же проверок через <c>SearchByFaceQuery.Handler</c> (состав
/// <c>ObjectRef</c>/<c>PayloadSensitive</c> записи, вырезка пробы в категории проб, отказ по чужому
/// основанию, проба-лицо носителя) требует ссылки тестового проекта на
/// <c>ISC.AI.Modules.Media.Application</c>: сейчас она доступна хосту только при
/// <c>IscProfile=investigation</c>, и в сборке по умолчанию сценарий тестам не виден.
/// </remarks>
[Trait("Category", "Gate")]
public sealed class FaceSearchAuditTests : IAsyncLifetime
{
    private const int CaseId = 3;
    private const short CaseClassification = 2;
    private const int DivisionId = 7;
    private const int SubjectId = 10;

    /// <summary>Предел копии пробы в аудите по умолчанию (<c>MediaSearchOptions.DefaultProbeCopyMaxBytes</c>): 4 МБ.</summary>
    private const long ProbeCopyMaxBytes = 4L * 1024 * 1024;

    private static readonly AccessContext Subject =
        new(SubjectId.ToString(CultureInfo.InvariantCulture), CaseClassification, [DivisionId]);

    private readonly PostgreSqlContainer _postgres = TestPostgres.Create();

    public Task InitializeAsync() => _postgres.StartAsync();

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    [Fact(DisplayName = "ТБ-074: поиск по вектору пробы и запись сессии не добавляют строк в face_template/face; сессия хранит только хеш")]
    public async Task Search_and_session_do_not_grow_template_base()
    {
        var media = new MediaContextFactory(_postgres.GetConnectionString());
        var vector = UnitVector();
        int assetId, faceId;
        await using (var db = media.CreateDbContext())
        {
            await db.Database.MigrateAsync();
            (assetId, faceId) = await SeedFaceWithTemplateAsync(db, vector);
        }

        var (templatesBefore, facesBefore) = await CountAsync(media);

        // Тот же путь, что у сценария поиска: вектор пробы — только в памяти запроса; область — носители дела.
        var policy = new AllowAllAccessPolicy();
        var candidates = await new PgVectorFaceSearch(media, policy).SearchAsync(
            new FaceSearchQuery(vector, TopK: 20, AssetIds: [assetId]), Subject);
        candidates.ShouldHaveSingleItem().FaceId.ShouldBe(faceId);

        var probeSha = Convert.ToHexStringLower(SHA256.HashData(new byte[] { 1, 2, 3 }));
        var store = new SearchSessionStore(media, policy);
        var sessionId = await store.CreateAsync(Draft(probeSha), candidates);
        (await store.ListCandidatesAsync(sessionId, Subject)).ShouldHaveSingleItem().FaceId.ShouldBe(faceId);

        // База шаблонов и лиц не выросла — проба сравнивалась, но не «оседала».
        var (templatesAfter, facesAfter) = await CountAsync(media);
        templatesAfter.ShouldBe(templatesBefore);
        facesAfter.ShouldBe(facesBefore);

        await using (var db = media.CreateDbContext())
        {
            var session = await db.SearchSessions.AsNoTracking().SingleAsync(s => s.Id == sessionId);
            session.ProbeSha256.ShouldBe(probeSha);
            session.Classification.ShouldBe(CaseClassification);
            session.DivisionId.ShouldBe(DivisionId);
            session.RequestedByUserId.ShouldBe(SubjectId);
        }
    }

    [Fact(DisplayName = "ТБ-074 на уровне схемы: единственная таблица media со столбцом vector — face_template; у сессий и кандидатов вектора нет")]
    public async Task Only_face_template_has_vector_column()
    {
        var media = new MediaContextFactory(_postgres.GetConnectionString());
        await using var db = media.CreateDbContext();
        await db.Database.MigrateAsync();

        // Страховка инварианта ТБ-074 от будущих миграций: столбец для вектора пробы в search_session /
        // search_candidate не появится незамеченным.
        var tablesWithVector = await db.Database
            .SqlQueryRaw<string>(
                "SELECT DISTINCT table_name AS \"Value\" FROM information_schema.columns " +
                "WHERE table_schema = 'media' AND udt_name = 'vector'")
            .ToListAsync();

        tablesWithVector.ShouldBe(["face_template"]);
    }

    [Fact(DisplayName = "ТБ-072: запись поиска с копией пробы 4 МБ (base64) сохраняется целиком и читается только в допуске и подразделении дела")]
    public async Task Search_audit_record_with_probe_copy_persists_under_case_grid()
    {
        var core = new CoreContextFactory(_postgres.GetConnectionString());
        await using (var db = core.CreateDbContext())
        {
            await db.Database.MigrateAsync();
        }

        // Копия пробы предельного размера — как её кладёт сценарий (base64 ≈ 5,6 МБ текста).
        var probe = new byte[ProbeCopyMaxBytes];
        for (var i = 0; i < probe.Length; i++)
        {
            probe[i] = unchecked((byte)i);
        }

        var probeSha = Convert.ToHexStringLower(SHA256.HashData(probe));
        var payload = "основание: Постановление № 5 (id 9)\n"
            + "проба: sha256=" + probeSha + "; изображение\n"
            + "копия пробы (base64, " + probe.Length.ToString(CultureInfo.InvariantCulture) + " байт): "
            + Convert.ToBase64String(probe) + "\n"
            + "модели: детектор yunet-test; векторизатор sface-test\n"
            + "параметры: topK=20; порог(cos-dist)=нет; ef_search=200\n"
            + "кандидаты (1): 1:100:50:-:0.9000";
        const string objectRef = "media:search:1;case:3;auth:9";

        await new AuditWriter(core).WriteAsync(new AuditEntry(
            AuditAction.Search,
            CaseClassification,
            SubjectId,
            ObjectRef: objectRef,
            DivisionId: DivisionId,
            PayloadSensitive: payload));

        var reader = new AuditReader(core);
        var filter = new AuditFilter(Action: AuditAction.Search, ObjectRef: objectRef);

        // Субъект дела: запись видна целиком — хеш, копия, кандидат-лист.
        var page = await reader.QueryAsync(filter, Subject);
        page.TotalCount.ShouldBe(1);
        var row = page.Rows.ShouldHaveSingleItem();
        row.SubjectId.ShouldBe(SubjectId);
        row.Classification.ShouldBe(CaseClassification);
        row.DivisionId.ShouldBe(DivisionId);
        row.ObjectRef.ShouldBe(objectRef);
        var stored = row.PayloadSensitive.ShouldNotBeNull();
        stored.Length.ShouldBe(payload.Length);
        stored.ShouldContain(probeSha);
        stored.ShouldEndWith("1:100:50:-:0.9000");

        // Ниже допуска либо чужое подразделение — записи как будто нет (ТБ-032, неразличимость).
        (await reader.QueryAsync(filter, new AccessContext("11", MaxClassification: 1, AllowedDivisions: [DivisionId]))).TotalCount.ShouldBe(0);
        (await reader.QueryAsync(filter, new AccessContext("12", MaxClassification: 9, AllowedDivisions: [8]))).TotalCount.ShouldBe(0);
    }

    // ---------- обвязка ----------

    private static float[] UnitVector()
    {
        var v = new float[FaceTemplate.Dimensions];
        v[0] = 1f;
        return v;
    }

    private static SearchSessionDraft Draft(string probeSha) => new(
        CaseId: CaseId,
        AuthorizationRef: "Постановление № 5",
        Scope: SearchScopeKind.CurrentCase,
        CaseIds: [CaseId],
        ProbeSha256: probeSha,
        ProbeFaceId: null,
        ProbeCropStoredFileName: null,
        TopK: 20,
        MaxCosineDistance: null,
        DetectorVersion: "yunet-test",
        EmbedderVersion: "sface-test",
        HnswEfSearch: 200,
        Classification: CaseClassification,
        DivisionId: DivisionId,
        RequestedByUserId: SubjectId);

    private static async Task<(int Templates, int Faces)> CountAsync(MediaContextFactory media)
    {
        await using var db = media.CreateDbContext();
        return (await db.Templates.CountAsync(), await db.Faces.CountAsync());
    }

    private static async Task<(int AssetId, int FaceId)> SeedFaceWithTemplateAsync(MediaDbContext db, float[] vector)
    {
        var asset = new MediaAsset
        {
            Kind = MediaKind.Image,
            OriginalFileName = "scene.jpg",
            StoredFileName = Guid.NewGuid().ToString("N") + ".jpg",
            ContentType = "image/jpeg",
            ContentHash = Guid.NewGuid().ToString("N"),
            ByteSize = 1,
            Classification = CaseClassification,
            DivisionId = DivisionId,
            IsCurrent = true,
            IndexStatus = MediaIndexStatus.Indexed,
        };
        db.Assets.Add(asset);
        await db.SaveChangesAsync();

        var face = new Face
        {
            AssetId = asset.Id,
            BoxX = 1, BoxY = 1, BoxWidth = 50, BoxHeight = 50,
            Landmarks = new float[10],
            DetectionScore = 0.95f,
            QualityScore = 0.9f,
            QualityAcceptable = true,
            CropStoredFileName = Guid.NewGuid().ToString("N") + ".jpg",
            Classification = CaseClassification,
            DivisionId = DivisionId,
        };
        db.Faces.Add(face);
        await db.SaveChangesAsync();

        db.Templates.Add(new FaceTemplate
        {
            FaceId = face.Id,
            AssetId = asset.Id,
            Embedding = new Vector(vector),
            ModelVersion = "sface-test",
            QualityAcceptable = true,
            Classification = CaseClassification,
            DivisionId = DivisionId,
            IsCurrent = true,
        });
        await db.SaveChangesAsync();
        return (asset.Id, face.Id);
    }
}
