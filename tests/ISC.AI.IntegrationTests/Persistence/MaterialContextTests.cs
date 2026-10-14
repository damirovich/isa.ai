using System;
using System.Linq;
using System.Threading.Tasks;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.Media.Data;
using ISC.AI.Modules.Media.Data.Entities;
using ISC.AI.Modules.Media.Domain.Model;
using ISC.AI.Profile.Investigation.Data;
using ISC.AI.Profile.Investigation.Domain.Enums;
using ISC.AI.Profile.Investigation.Domain.Services;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Testcontainers.PostgreSql;

namespace ISC.AI.IntegrationTests.Persistence;

/// <summary>
/// Сведения о материале кандидата (ТФ-ПЛ-02) на настоящем Postgres: время съёмки, загрузки и источник — только у
/// носителей в допуске; место — из привязок к делам в пределах допуска (и чужим — взаимный поиск, ADR-0044), только
/// непустое; без роли — ничего.
/// </summary>
/// <remarks>Требуется Docker.</remarks>
public sealed class MaterialContextTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = TestPostgres.Create();

    public Task InitializeAsync() => _postgres.StartAsync();

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    [Fact(DisplayName = "Каталог: время съёмки, загрузки и источник — только у носителей в допуске")]
    public async Task Material_info_respects_access()
    {
        var factory = new MediaContextFactory(_postgres.GetConnectionString());
        int visible, secret;
        var captured = new DateTimeOffset(2022, 12, 8, 14, 8, 0, TimeSpan.Zero);
        await using (var db = factory.CreateDbContext())
        {
            await db.Database.MigrateAsync();
            visible = await SeedAssetAsync(db, classification: 1, captured, "Материалы ОРМ");
            secret = await SeedAssetAsync(db, classification: 3, null, "Секретный источник");
        }

        var catalog = new MediaCatalog(factory, new AllowAllAccessPolicy());
        var infos = await catalog.ListMaterialInfoAsync([visible, secret], new AccessContext("10", 1, [7]));

        var info = infos.ShouldHaveSingleItem().Value;
        info.AssetId.ShouldBe(visible);
        info.CapturedAt.ShouldBe(captured);
        info.Source.ShouldBe("Материалы ОРМ");
        info.UploadedAtUtc.Kind.ShouldBe(DateTimeKind.Utc);
        (await catalog.ListMaterialInfoAsync([], new AccessContext("10", 1, [7]))).ShouldBeEmpty();
    }

    [Fact(DisplayName = "Место: из привязки к делу в пределах допуска, в том числе чужому (ADR-0044); дело выше допуска, пустое место и субъект без роли не дают ничего")]
    public async Task Places_come_from_cases_within_clearance_only()
    {
        var factory = new InvestigationContextFactory(_postgres.GetConnectionString());
        var core = new CoreContextFactory(_postgres.GetConnectionString());
        await InvestigationTestKit.MigrateAsync(factory);
        await InvestigationTestKit.AssignRolesAsync(factory, (10, InvestigationRole.Investigator), (11, InvestigationRole.Investigator));

        var cases = InvestigationTestKit.CreateCaseStore(factory, core);
        var scope = InvestigationTestKit.CreateCaseScope(factory, core);
        var owner = InvestigationTestKit.Access(10, 4, 5);

        var open = (await cases.CreateAsync(InvestigationTestKit.Draft("М-1", 5, 2, 10), owner)).CaseId;
        var secret = (await cases.CreateAsync(InvestigationTestKit.Draft("М-2", 5, 4, 10), owner)).CaseId;
        (await cases.LinkMediaAsync(open, 100, " г. Бишкек ", 10)).ShouldBe(CaseWriteResult.Ok);
        (await cases.LinkMediaAsync(secret, 101, "г. Ош", 10)).ShouldBe(CaseWriteResult.Ok);
        (await cases.LinkMediaAsync(open, 102, null, 10)).ShouldBe(CaseWriteResult.Ok);

        // Дело коллеги (сотрудник 11) того же подразделения: материал кандидата взаимного поиска — с местом.
        var colleague = InvestigationTestKit.Access(11, 4, 5);
        var foreign = (await cases.CreateAsync(InvestigationTestKit.Draft("М-3", 5, 2, 11, 11), colleague)).CaseId;
        (await cases.LinkMediaAsync(foreign, 103, "г. Ош, ул. Примерная, 7", 11)).ShouldBe(CaseWriteResult.Ok);

        // Допуск «Секретно» (2): дело с грифом 4 сотруднику не видно — его место не выдаётся.
        var places = await scope.ListCandidatePlacesAsync([100, 101, 102, 103], InvestigationTestKit.Access(10, 2, 5));

        places.Keys.Order().ToList().ShouldBe([100, 103]);
        places[100].ShouldBe("г. Бишкек");
        places[103].ShouldBe("г. Ош, ул. Примерная, 7");
        (await scope.ListCandidatePlacesAsync([100], InvestigationTestKit.Access(99, 4, 5))).ShouldBeEmpty(); // без роли
    }

    private static async Task<int> SeedAssetAsync(MediaDbContext db, short classification, DateTimeOffset? captured, string source)
    {
        var asset = new MediaAsset
        {
            Kind = MediaKind.Image,
            OriginalFileName = "a.jpg",
            StoredFileName = Guid.NewGuid().ToString("N") + ".jpg",
            ContentType = "image/jpeg",
            ContentHash = Guid.NewGuid().ToString("N"),
            ByteSize = 1,
            Classification = classification,
            DivisionId = 7,
            IndexStatus = MediaIndexStatus.Indexed,
            CapturedAt = captured,
            Source = source,
        };
        db.Assets.Add(asset);
        await db.SaveChangesAsync();
        return asset.Id;
    }
}
