using System;
using System.Linq;
using System.Threading.Tasks;
using ISC.AI.Modules.Media.Data;
using ISC.AI.Modules.Media.Data.Entities;
using ISC.AI.Modules.Media.Domain.Model;
using ISC.AI.Profile.Investigation.Data;
using ISC.AI.Profile.Investigation.Domain.Entities;
using ISC.AI.Profile.Investigation.Domain.Enums;
using ISC.AI.Profile.Investigation.Domain.Services;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Testcontainers.PostgreSql;

namespace ISC.AI.IntegrationTests.Persistence;

/// <summary>
/// Пересечения между делами (ТФ-ПЕР-07, ТБ-084, ADR-0029) на настоящем Postgres: совпадения нормализованного
/// госномера, адреса и ФИО + даты рождения находятся с делом коллеги в пределах допуска; дело выше допуска и
/// чужого подразделения не раскрывается никак; совпадение внутри своего дела — не пересечение; признак «можно
/// открыть» следует ТФ-ДЕЛ-03; решение пишется только по видимому пересечению и перезаписывается повторным.
/// </summary>
public sealed class InvestigationIntersectionsTests : IAsyncLifetime
{
    private const int Owner = 10;
    private const int Colleague = 11;
    private const int Head = 20;

    private readonly PostgreSqlContainer _postgres = TestPostgres.Create();

    public Task InitializeAsync() => _postgres.StartAsync();

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    [Fact(DisplayName = "Пересечения: дело коллеги в пределах допуска — номер, реквизит, ответственный; выше допуска, чужое подразделение и своё дело — ничего")]
    public async Task Intersections_respect_clearance_and_division()
    {
        var kit = await ArrangeAsync();
        var scene = await SeedAsync(kit);

        // Следователь: допуск 3, подразделение 5 — видит дело коллеги, но не может открыть его по роли.
        var viewer = InvestigationTestKit.Access(Owner, 3, 5);
        var rows = (await kit.Intersections.FindForPersonAsync(scene.Target, viewer)).ShouldNotBeNull();

        rows.Select(r => r.OtherCaseId).Distinct().ShouldBe([scene.ColleagueCase]);

        var vehicle = rows.Single(r => r.Kind == IntersectionKind.Vehicle);
        vehicle.Key.ShouldBe("01KG123ABC");
        vehicle.OwnValue.ShouldBe("01 KG 123 ABC");
        vehicle.OtherCaseNumber.ShouldBe("Б-19/26");
        vehicle.ResponsibleUserId.ShouldBe(Colleague);
        vehicle.CanOpenCase.ShouldBeFalse();
        vehicle.Decision.ShouldBeNull();

        // Адрес строки сошёлся с местом жительства из анкеты фигуранта другого дела.
        rows.Single(r => r.Kind == IntersectionKind.Address).Key.ShouldBe("бишкек ул токтогула 1");

        // ФИО + дата рождения: однофамилец с другой датой пересечением не считается.
        var name = rows.Single(r => r.Kind == IntersectionKind.PersonName);
        name.OtherValue.ShouldBe("Семёнов Пётр, 01.02.1990");

        // Руководитель подразделения видит те же пересечения и может открыть дело (ТФ-ДЕЛ-03).
        var head = (await kit.Intersections.FindForPersonAsync(scene.Target, InvestigationTestKit.Access(Head, 3, 5))).ShouldNotBeNull();
        head.Count.ShouldBe(rows.Count);
        head.ShouldAllBe(r => r.CanOpenCase);

        // С широким допуском (4, подразделения 5 и 6) проявляются и скрытые дела — значит, скрывал именно floor.
        var wide = (await kit.Intersections.FindForPersonAsync(scene.Target, InvestigationTestKit.Access(Head, 4, 5, 6))).ShouldNotBeNull();
        wide.Select(r => r.OtherCaseId).Distinct().Order().ShouldBe(new[] { scene.ColleagueCase, scene.SecretCase, scene.OtherDivisionCase }.Order());

        // Фигурант недоступен по роли (чужой следователь без дела) — «не найден», а не пустой список.
        (await kit.Intersections.FindForPersonAsync(scene.Target, InvestigationTestKit.Access(Colleague, 3, 5))).ShouldBeNull();
    }

    [Fact(DisplayName = "Решение по пересечению: пишется строкой своего дела и перезаписывается; по невидимому пересечению — NotFound")]
    public async Task Review_is_written_only_for_visible_intersection()
    {
        var kit = await ArrangeAsync();
        var scene = await SeedAsync(kit);
        var viewer = InvestigationTestKit.Access(Owner, 3, 5);

        (await kit.Intersections.ReviewAsync(scene.Target, IntersectionKind.Vehicle, "01KG123ABC", scene.ColleagueCase,
            IntersectionDecision.Confirmed, viewer)).ShouldBe(PersonWriteResult.Ok);
        (await kit.Intersections.ReviewAsync(scene.Target, IntersectionKind.Vehicle, "01KG123ABC", scene.ColleagueCase,
            IntersectionDecision.Rejected, viewer)).ShouldBe(PersonWriteResult.Ok);

        var row = (await kit.Intersections.FindForPersonAsync(scene.Target, viewer)).ShouldNotBeNull()
            .Single(r => r.Kind == IntersectionKind.Vehicle);
        row.Decision.ShouldBe(IntersectionDecision.Rejected);
        row.DecidedByUserId.ShouldBe(Owner);
        row.DecidedAt.ShouldNotBeNull();

        await using (var db = kit.Factory.CreateDbContext())
        {
            var stored = await db.IntersectionReviews.AsNoTracking().SingleAsync();
            stored.CaseId.ShouldBe(scene.OwnCase);
            stored.OtherCaseId.ShouldBe(scene.ColleagueCase);
            stored.Classification.ShouldBe<short>(2);
            stored.DivisionId.ShouldBe(5);
        }

        // Дело выше допуска, несуществующее дело, чужой ключ — один и тот же ответ, строк не прибавляется.
        (await kit.Intersections.ReviewAsync(scene.Target, IntersectionKind.Vehicle, "01KG123ABC", scene.SecretCase,
            IntersectionDecision.Confirmed, viewer)).ShouldBe(PersonWriteResult.NotFound);
        (await kit.Intersections.ReviewAsync(scene.Target, IntersectionKind.Vehicle, "01KG123ABC", 999_999,
            IntersectionDecision.Confirmed, viewer)).ShouldBe(PersonWriteResult.NotFound);
        (await kit.Intersections.ReviewAsync(scene.Target, IntersectionKind.Vehicle, "XXX", scene.ColleagueCase,
            IntersectionDecision.Confirmed, viewer)).ShouldBe(PersonWriteResult.NotFound);
        (await kit.Intersections.ReviewAsync(scene.Target, IntersectionKind.Vehicle, "01KG123ABC", scene.ColleagueCase,
            IntersectionDecision.Confirmed, InvestigationTestKit.Access(Colleague, 3, 5))).ShouldBe(PersonWriteResult.NotFound);

        await using (var db = kit.Factory.CreateDbContext())
        {
            (await db.IntersectionReviews.CountAsync()).ShouldBe(1);
        }

        // Удаление своего фигуранта уносит решение (каскад), чужое дело не затронуто.
        await using (var db = kit.Factory.CreateDbContext())
        {
            await db.Persons.Where(p => p.Id == scene.Target).ExecuteDeleteAsync();
            (await db.IntersectionReviews.CountAsync()).ShouldBe(0);
            (await db.Cases.CountAsync(c => c.Id == scene.ColleagueCase)).ShouldBe(1);
        }
    }

    [Fact(DisplayName = "Пересечение по лицу: объект на материале чужого дела и то же лицо у чужого фигуранта; выше допуска, чужое подразделение и своё дело — ничего")]
    public async Task Face_intersections_respect_clearance_and_own_case()
    {
        var kit = await ArrangeAsync();
        var owner = InvestigationTestKit.Access(Owner, 9, 5);
        var colleague = InvestigationTestKit.Access(Colleague, 9, 5, 6);

        var ownCase = (await kit.Cases.CreateAsync(InvestigationTestKit.Draft("А-1/26", 5, 2, Owner), owner)).CaseId;
        var colleagueCase = (await kit.Cases.CreateAsync(InvestigationTestKit.Draft("Б-19/26", 5, 2, Colleague), colleague)).CaseId;
        var secretCase = (await kit.Cases.CreateAsync(InvestigationTestKit.Draft("С-7/26", 5, 4, Colleague), colleague)).CaseId;
        var otherDivisionCase = (await kit.Cases.CreateAsync(InvestigationTestKit.Draft("Д-3/26", 6, 2, Colleague), colleague)).CaseId;

        var target = await CreatePersonAsync(kit, owner, ownCase, "Объект", null);
        var sameCaseLink = await CreatePersonAsync(kit, owner, ownCase, "Связь", null);
        var colleaguePerson = await CreatePersonAsync(kit, colleague, colleagueCase, "Шторм", null);
        var secretPerson = await CreatePersonAsync(kit, colleague, secretCase, "Скрытый", null);
        var otherDivisionPerson = await CreatePersonAsync(kit, colleague, otherDivisionCase, "Чужой", null);

        const int SharedAsset = 500, OwnOnlyAsset = 501;
        const int SharedFace = 900, OwnOnlyFace = 901;
        await using (var db = kit.Factory.CreateDbContext())
        {
            // Носитель 500 — в своём деле и (после дедупликации) в делах коллеги, секретном и чужого подразделения.
            foreach (var caseId in new[] { ownCase, colleagueCase, secretCase, otherDivisionCase })
            {
                db.CaseMediaLinks.Add(new CaseMediaLink { CaseId = caseId, MediaAssetId = SharedAsset });
            }

            db.CaseMediaLinks.Add(new CaseMediaLink { CaseId = ownCase, MediaAssetId = OwnOnlyAsset });

            var candidate = 1;
            db.Appearances.AddRange(
                Appearance(target, ownCase, SharedAsset, SharedFace, candidate++, 2, 5, new DateTime(2026, 9, 20, 10, 0, 0, DateTimeKind.Utc)),
                Appearance(target, ownCase, OwnOnlyAsset, OwnOnlyFace, candidate++, 2, 5, new DateTime(2026, 9, 21, 10, 0, 0, DateTimeKind.Utc)),
                // То же лицо: у связи СВОЕГО дела (не пересечение), у фигуранта коллеги, у скрытых.
                Appearance(sameCaseLink, ownCase, SharedAsset, SharedFace, candidate++, 2, 5, DateTime.UtcNow),
                Appearance(colleaguePerson, colleagueCase, SharedAsset, SharedFace, candidate++, 2, 5, DateTime.UtcNow),
                Appearance(secretPerson, secretCase, SharedAsset, SharedFace, candidate++, 4, 5, DateTime.UtcNow),
                Appearance(otherDivisionPerson, otherDivisionCase, SharedAsset, SharedFace, candidate++, 2, 6, DateTime.UtcNow));
            await db.SaveChangesAsync();
        }

        var viewer = InvestigationTestKit.Access(Owner, 3, 5);
        var rows = (await kit.Intersections.FindForPersonAsync(target, viewer)).ShouldNotBeNull()
            .Where(r => r.Kind == IntersectionKind.Face).ToList();

        rows.Select(r => r.OtherCaseId).Distinct().ShouldBe([colleagueCase]);
        rows.Select(r => r.Key).Order().ShouldBe(["face:material", "face:shared"]);
        rows.Single(r => r.Key == "face:material").OtherValue.ShouldContain("появлений 1, последнее 20.09.2026");
        rows.Single(r => r.Key == "face:shared").OtherValue.ShouldContain("совпавших появлений объекта 1");
        rows.ShouldAllBe(r => r.OwnValue.StartsWith("лицо объекта") && r.ResponsibleUserId == Colleague && !r.CanOpenCase);
        rows.ShouldAllBe(r => !r.OtherValue.Contains("Шторм")); // имя чужого фигуранта не раскрывается (ТБ-084)

        // С допуском 4 и подразделениями 5, 6 проявляются и скрытые дела — значит, скрывал именно floor.
        var wide = (await kit.Intersections.FindForPersonAsync(target, InvestigationTestKit.Access(Owner, 4, 5, 6))).ShouldNotBeNull()
            .Where(r => r.Kind == IntersectionKind.Face).Select(r => r.OtherCaseId).Distinct().Order();
        wide.ShouldBe(new[] { colleagueCase, secretCase, otherDivisionCase }.Order());

        // Решение по пересечению по лицу — как по остальным; по невидимому делу — «не найдено».
        (await kit.Intersections.ReviewAsync(target, IntersectionKind.Face, "face:shared", colleagueCase, IntersectionDecision.Confirmed, viewer))
            .ShouldBe(PersonWriteResult.Ok);
        (await kit.Intersections.ReviewAsync(target, IntersectionKind.Face, "face:shared", secretCase, IntersectionDecision.Confirmed, viewer))
            .ShouldBe(PersonWriteResult.NotFound);
        (await kit.Intersections.FindForPersonAsync(target, viewer)).ShouldNotBeNull()
            .Single(r => r.Kind == IntersectionKind.Face && r.Key == "face:shared").Decision.ShouldBe(IntersectionDecision.Confirmed);

        // Со стороны фигуранта коллеги — зеркальное пересечение с делом объекта.
        var mirrored = (await kit.Intersections.FindForPersonAsync(colleaguePerson, InvestigationTestKit.Access(Colleague, 3, 5))).ShouldNotBeNull()
            .Where(r => r.Kind == IntersectionKind.Face).ToList();
        mirrored.ShouldContain(r => r.Key == "face:shared" && r.OtherCaseId == ownCase);
    }

    [Fact(DisplayName = "Пересечение по лицу: тот же файл в деле с другим грифом (отдельный носитель, тот же хеш) — находится в пределах допуска")]
    public async Task Face_intersection_finds_same_file_uploaded_under_other_classification()
    {
        var kit = await ArrangeAsync();
        var owner = InvestigationTestKit.Access(Owner, 9, 5);
        var colleague = InvestigationTestKit.Access(Colleague, 9, 5);

        // Своё дело — ДСП (1), дело коллеги — «Секретно» (3), скрытое — выше допуска просмотра (4).
        var ownCase = (await kit.Cases.CreateAsync(InvestigationTestKit.Draft("978", 5, 1, Owner), owner)).CaseId;
        var secretCase = (await kit.Cases.CreateAsync(InvestigationTestKit.Draft("312312312", 5, 3, Colleague), colleague)).CaseId;
        var hiddenCase = (await kit.Cases.CreateAsync(InvestigationTestKit.Draft("ОВ-1/26", 5, 4, Colleague), colleague)).CaseId;
        var target = await CreatePersonAsync(kit, owner, ownCase, "Объект", null);

        // Один снимок, загруженный в три дела разного грифа, — три носителя с одинаковым хешем (ТБ-070).
        var ownAsset = await MediaAssetAsync(kit, "SAMEPHOTO", 1, 5);
        var secretCopy = await MediaAssetAsync(kit, "SAMEPHOTO", 3, 5);
        var hiddenCopy = await MediaAssetAsync(kit, "SAMEPHOTO", 4, 5);
        var unrelated = await MediaAssetAsync(kit, "OTHERPHOTO", 3, 5);
        await using (var db = kit.Factory.CreateDbContext())
        {
            db.CaseMediaLinks.AddRange(
                new CaseMediaLink { CaseId = ownCase, MediaAssetId = ownAsset },
                new CaseMediaLink { CaseId = secretCase, MediaAssetId = secretCopy },
                new CaseMediaLink { CaseId = secretCase, MediaAssetId = unrelated },
                new CaseMediaLink { CaseId = hiddenCase, MediaAssetId = hiddenCopy });
            db.Appearances.Add(Appearance(target, ownCase, ownAsset, 900, 1, 1, 5, new DateTime(2026, 9, 18, 10, 0, 0, DateTimeKind.Utc)));
            await db.SaveChangesAsync();
        }

        // Допуск 3: копия в секретном деле видна — пересечение есть; дело с грифом 4 не проявляется.
        var viewer = InvestigationTestKit.Access(Owner, 3, 5);
        var rows = (await kit.Intersections.FindForPersonAsync(target, viewer)).ShouldNotBeNull()
            .Where(r => r.Kind == IntersectionKind.Face).ToList();
        var row = rows.ShouldHaveSingleItem();
        row.Key.ShouldBe("face:material");
        row.OtherCaseId.ShouldBe(secretCase);
        row.OtherValue.ShouldContain("появлений 1, последнее 18.09.2026");

        // Допуск 1: копия и дело «Секретно» вне допуска — ничего, как будто их нет.
        (await kit.Intersections.FindForPersonAsync(target, InvestigationTestKit.Access(Owner, 1, 5))).ShouldNotBeNull()
            .ShouldNotContain(r => r.Kind == IntersectionKind.Face);

        // Допуск 4 открывает и скрытое дело — значит, скрывал именно floor.
        (await kit.Intersections.FindForPersonAsync(target, InvestigationTestKit.Access(Owner, 4, 5))).ShouldNotBeNull()
            .Where(r => r.Kind == IntersectionKind.Face).Select(r => r.OtherCaseId).Order()
            .ShouldBe(new[] { secretCase, hiddenCase }.Order());

        // Решение по такому пересечению принимается, как по остальным.
        (await kit.Intersections.ReviewAsync(target, IntersectionKind.Face, "face:material", secretCase, IntersectionDecision.Confirmed, viewer))
            .ShouldBe(PersonWriteResult.Ok);
    }

    private static async Task<int> MediaAssetAsync(Kit kit, string hash, short classification, int divisionId)
    {
        await using var db = kit.Media.CreateDbContext();
        var asset = new MediaAsset
        {
            Kind = MediaKind.Image,
            OriginalFileName = "photo.jpg",
            StoredFileName = Guid.NewGuid().ToString("N") + ".jpg",
            ContentType = "image/jpeg",
            ContentHash = hash,
            ByteSize = 1,
            Classification = classification,
            DivisionId = divisionId,
            IndexStatus = MediaIndexStatus.Indexed,
        };
        db.Assets.Add(asset);
        await db.SaveChangesAsync();
        return asset.Id;
    }

    private static Appearance Appearance(
        int personId, int caseId, int assetId, int faceId, int candidateId, short classification, int divisionId, DateTime confirmedAtUtc) => new()
    {
        PersonId = personId,
        CaseId = caseId,
        MediaAssetId = assetId,
        MediaFaceId = faceId,
        SearchSessionId = 1,
        CandidateId = candidateId,
        Similarity = 0.8,
        ConfirmedAtUtc = confirmedAtUtc,
        ExpertUserId = 1,
        VerifierUserId = 2,
        Classification = classification,
        DivisionId = divisionId,
    };

    private static async Task<Scene> SeedAsync(Kit kit)
    {
        var owner = InvestigationTestKit.Access(Owner, 9, 5);
        var colleague = InvestigationTestKit.Access(Colleague, 9, 5, 6);

        var ownCase = (await kit.Cases.CreateAsync(InvestigationTestKit.Draft("А-1/26", 5, 2, Owner), owner)).CaseId;
        var colleagueCase = (await kit.Cases.CreateAsync(InvestigationTestKit.Draft("Б-19/26", 5, 2, Colleague), colleague)).CaseId;
        var secretCase = (await kit.Cases.CreateAsync(InvestigationTestKit.Draft("С-7/26", 5, 4, Colleague), colleague)).CaseId;
        var otherDivisionCase = (await kit.Cases.CreateAsync(InvestigationTestKit.Draft("Д-3/26", 6, 2, Colleague), colleague)).CaseId;

        var birth = new DateOnly(1990, 2, 1);
        var target = await CreatePersonAsync(kit, owner, ownCase, "Семенов Петр", new PersonQuestionnaire(BirthDate: birth));
        await SaveVehicleAsync(kit, owner, target, "01 KG 123 ABC");
        (await kit.Requisites.SaveAddressAsync(target, null, new PersonAddressDraft(AddressKind.Residence, "Бишкек, улица Токтогула 1", null), owner))
            .Result.ShouldBe(PersonWriteResult.Ok);

        // Совпадение внутри своего дела — не пересечение.
        var sameCase = await CreatePersonAsync(kit, owner, ownCase, "Связь", null);
        await SaveVehicleAsync(kit, owner, sameCase, "01KG123ABC");

        // Дело коллеги: два фигуранта с тем же номером (одна строка), место жительства, тёзка с той же датой и с другой.
        var shtorm = await CreatePersonAsync(kit, colleague, colleagueCase, "Шторм",
            new PersonQuestionnaire(Residence: "г. Бишкек, ул. Токтогула, д. 1"));
        await SaveVehicleAsync(kit, colleague, shtorm, "01kg123авс");
        var second = await CreatePersonAsync(kit, colleague, colleagueCase, "Второй", null);
        await SaveVehicleAsync(kit, colleague, second, "01-KG-123-ABC");
        await CreatePersonAsync(kit, colleague, colleagueCase, "Семёнов Пётр", new PersonQuestionnaire(BirthDate: birth));
        await CreatePersonAsync(kit, colleague, colleagueCase, "Семенов Петр", new PersonQuestionnaire(BirthDate: new DateOnly(1985, 5, 5)));

        // Выше допуска и чужое подразделение — те же реквизиты.
        foreach (var hidden in new[] { secretCase, otherDivisionCase })
        {
            var person = await CreatePersonAsync(kit, colleague, hidden, "Скрытый", new PersonQuestionnaire(BirthDate: birth));
            await SaveVehicleAsync(kit, colleague, person, "01KG123ABC");
        }

        return new Scene(ownCase, colleagueCase, secretCase, otherDivisionCase, target);
    }

    private static async Task<int> CreatePersonAsync(Kit kit, ISC.AI.Abstractions.Security.AccessContext access, int caseId, string name, PersonQuestionnaire? questionnaire)
    {
        var (result, id) = await kit.Persons.CreateAsync(
            new PersonDraft(caseId, name, false, null, null, PersonRole.Target, questionnaire), access);
        result.ShouldBe(PersonWriteResult.Ok);
        return id;
    }

    private static async Task SaveVehicleAsync(Kit kit, ISC.AI.Abstractions.Security.AccessContext access, int personId, string plate) =>
        (await kit.Requisites.SaveVehicleAsync(personId, null, new PersonVehicleDraft(plate, null, null, null, null), access))
            .Result.ShouldBe(PersonWriteResult.Ok);

    private async Task<Kit> ArrangeAsync()
    {
        var factory = new InvestigationContextFactory(_postgres.GetConnectionString());
        var core = new CoreContextFactory(_postgres.GetConnectionString());
        await InvestigationTestKit.MigrateAsync(factory);
        await InvestigationTestKit.AssignRolesAsync(factory,
            (Owner, InvestigationRole.Investigator), (Colleague, InvestigationRole.Investigator), (Head, InvestigationRole.Head));
        var media = new MediaContextFactory(_postgres.GetConnectionString());
        await using (var mediaDb = media.CreateDbContext())
        {
            await mediaDb.Database.MigrateAsync();
        }

        var policy = new InvestigationAccessPolicy(factory);
        var roles = new UserRoleStore(core, factory);
        return new Kit(
            factory,
            media,
            InvestigationTestKit.CreateCaseStore(factory, core),
            InvestigationTestKit.CreatePersonStore(factory, core),
            new PersonRequisiteStore(factory, policy, roles),
            new IntersectionStore(factory, policy, roles, new MediaCatalog(media, policy)));
    }

    private sealed record Scene(int OwnCase, int ColleagueCase, int SecretCase, int OtherDivisionCase, int Target);

    private sealed record Kit(
        InvestigationContextFactory Factory,
        MediaContextFactory Media,
        CaseStore Cases,
        PersonStore Persons,
        PersonRequisiteStore Requisites,
        IntersectionStore Intersections);
}
