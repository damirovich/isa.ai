using System;
using System.Linq;
using System.Threading.Tasks;
using ISC.AI.Profile.Investigation.Data;
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
        var policy = new InvestigationAccessPolicy(factory);
        var roles = new UserRoleStore(core, factory);
        return new Kit(
            factory,
            InvestigationTestKit.CreateCaseStore(factory, core),
            InvestigationTestKit.CreatePersonStore(factory, core),
            new PersonRequisiteStore(factory, policy, roles),
            new IntersectionStore(factory, policy, roles));
    }

    private sealed record Scene(int OwnCase, int ColleagueCase, int SecretCase, int OtherDivisionCase, int Target);

    private sealed record Kit(
        InvestigationContextFactory Factory,
        CaseStore Cases,
        PersonStore Persons,
        PersonRequisiteStore Requisites,
        IntersectionStore Intersections);
}
