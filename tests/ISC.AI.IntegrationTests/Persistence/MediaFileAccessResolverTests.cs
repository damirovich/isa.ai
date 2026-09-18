using System.Globalization;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.Media.Data;
using ISC.AI.Modules.Media.Data.Entities;
using ISC.AI.Modules.Media.Domain.Model;
using ISC.AI.Modules.Media.Domain.Services;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Testcontainers.PostgreSql;

namespace ISC.AI.IntegrationTests.Persistence;

/// <summary>
/// Резолвер раздачи пакета «Медиа» (GATE-4, ТБ-070/073): каждая категория отдаёт режимные поля ВЛАДЕЛЬЦА
/// (носителя — для исходника и вырезки, сессии — для вырезки пробы), вырезка пробы разрешается только по
/// паре (сессия, имя) и несёт дело сессии (<c>CaseRef</c>) для проверки области дел субъекта; подмена
/// идентификатора в маршруте и неизвестная категория наружу неотличимы от «файла нет».
/// </summary>
/// <remarks>Требуется Docker.</remarks>
[Trait("Category", "Gate")]
public sealed class MediaFileAccessResolverTests : IAsyncLifetime
{
    private const string ProbeCrop = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.jpg";

    private readonly PostgreSqlContainer _postgres = TestPostgres.Create();

    public Task InitializeAsync() => _postgres.StartAsync();

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    [Fact(DisplayName = "GATE-4: вырезка пробы — только по (сессия, имя), с грифом/подразделением/делом сессии; чужая сессия, чужое имя, чужая категория — null")]
    public async Task Probe_crop_resolves_only_by_session_and_name_with_session_access_fields()
    {
        var factory = new MediaContextFactory(_postgres.GetConnectionString());
        await using (var db = factory.CreateDbContext())
        {
            await db.Database.MigrateAsync();
        }

        var store = new SearchSessionStore(factory, new AllowAllAccessPolicy());
        var sessionA = await store.CreateAsync(Draft(caseId: 100, classification: 2, divisionId: 7, probeCrop: ProbeCrop), []);
        var sessionB = await store.CreateAsync(Draft(caseId: 200, classification: 1, divisionId: 7, probeCrop: null), []);

        var resolver = new MediaFileAccessResolver(factory);

        var file = await resolver.ResolveAsync(MediaFileCategories.Probes, sessionA, ProbeCrop);
        file.ShouldNotBeNull();
        file.Category.ShouldBe(MediaFileCategories.Probes);
        file.StoredFileName.ShouldBe(ProbeCrop);
        file.ContentType.ShouldBe("image/jpeg");
        file.Classification.ShouldBe<short>(2);
        file.DivisionId.ShouldBe(7);
        file.SubPath.ShouldBe("100"); // подкаталог — дело (известно до создания сессии)
        file.CaseRef.ShouldBe(100); // дело сессии — для проверки области дел субъекта в эндпоинте (ТБ-071)

        // Подмена сессии в маршруте при известном имени файла, чужое имя, неизвестная категория — единый null.
        (await resolver.ResolveAsync(MediaFileCategories.Probes, sessionB, ProbeCrop)).ShouldBeNull();
        (await resolver.ResolveAsync(MediaFileCategories.Probes, sessionA, "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb.jpg")).ShouldBeNull();
        (await resolver.ResolveAsync("media-unknown", sessionA, ProbeCrop)).ShouldBeNull();

        // Режимные поля дескриптора участвуют в floor эндпоинта: допуск ниже грифа и чужое подразделение — отказ.
        BaselineAccess.Filter<MediaFileDescriptor>(new AccessContext("u", 1, [7])).Compile()(file).ShouldBeFalse();
        BaselineAccess.Filter<MediaFileDescriptor>(new AccessContext("u", 2, [8])).Compile()(file).ShouldBeFalse();
        BaselineAccess.Filter<MediaFileDescriptor>(new AccessContext("u", 2, [7])).Compile()(file).ShouldBeTrue();
    }

    [Fact(DisplayName = "GATE-4: исходник и вырезка лица — режимные поля носителя, CaseRef пуст; файл чужого носителя в маршруте — null")]
    public async Task Original_and_face_crop_resolve_by_owning_asset_only()
    {
        var factory = new MediaContextFactory(_postgres.GetConnectionString());
        int assetA, assetB;
        string originalA, cropA;
        await using (var db = factory.CreateDbContext())
        {
            await db.Database.MigrateAsync();
            (assetA, originalA, cropA) = await SeedAssetWithFaceAsync(db, classification: 2, divisionId: 7);
            (assetB, _, _) = await SeedAssetWithFaceAsync(db, classification: 1, divisionId: 7);
        }

        var resolver = new MediaFileAccessResolver(factory);

        var original = await resolver.ResolveAsync(MediaFileCategories.Originals, assetA, originalA);
        original.ShouldNotBeNull();
        original.ContentType.ShouldBe("image/png");
        original.Classification.ShouldBe<short>(2);
        original.DivisionId.ShouldBe(7);
        original.SubPath.ShouldBe(assetA.ToString(CultureInfo.InvariantCulture));
        original.CaseRef.ShouldBeNull(); // дело носителя определяет профиль по привязке (IsAssetAccessibleAsync)

        var crop = await resolver.ResolveAsync(MediaFileCategories.FaceCrops, assetA, cropA);
        crop.ShouldNotBeNull();
        crop.ContentType.ShouldBe("image/jpeg");
        crop.Classification.ShouldBe<short>(2);
        crop.SubPath.ShouldBe(assetA.ToString(CultureInfo.InvariantCulture));
        crop.CaseRef.ShouldBeNull();

        // Имя известно, но в маршруте другой носитель — файл «не существует» (защита от подмены маршрута).
        (await resolver.ResolveAsync(MediaFileCategories.Originals, assetB, originalA)).ShouldBeNull();
        (await resolver.ResolveAsync(MediaFileCategories.FaceCrops, assetB, cropA)).ShouldBeNull();
        // Категория не та, что у файла, — тоже null (исходник по маршруту вырезок и наоборот).
        (await resolver.ResolveAsync(MediaFileCategories.FaceCrops, assetA, originalA)).ShouldBeNull();
        (await resolver.ResolveAsync(MediaFileCategories.Originals, assetA, cropA)).ShouldBeNull();
    }

    private static SearchSessionDraft Draft(int caseId, short classification, int divisionId, string? probeCrop) => new(
        CaseId: caseId,
        AuthorizationRef: "поручение № 7",
        Scope: SearchScopeKind.CurrentCase,
        CaseIds: [caseId],
        ProbeSha256: new string('A', 64),
        ProbeFaceId: null,
        ProbeCropStoredFileName: probeCrop,
        TopK: 20,
        MaxCosineDistance: 0.6,
        DetectorVersion: "yunet-test",
        EmbedderVersion: "sface-test",
        HnswEfSearch: 200,
        Classification: classification,
        DivisionId: divisionId,
        RequestedByUserId: 10);

    private static async Task<(int AssetId, string Original, string Crop)> SeedAssetWithFaceAsync(
        MediaDbContext db, short classification, int divisionId)
    {
        var asset = new MediaAsset
        {
            Kind = MediaKind.Image,
            OriginalFileName = "a.png",
            StoredFileName = Guid.NewGuid().ToString("N") + ".png",
            ContentType = "image/png",
            ContentHash = Guid.NewGuid().ToString("N"),
            ByteSize = 1,
            Classification = classification,
            DivisionId = divisionId,
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
            Classification = classification,
            DivisionId = divisionId,
        };
        db.Faces.Add(face);
        await db.SaveChangesAsync();
        return (asset.Id, asset.StoredFileName, face.CropStoredFileName!);
    }
}
