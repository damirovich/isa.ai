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
/// Порт «Медиа» <c>ICaseScope.ListSuggestionTargetsAsync</c> на стороне профиля (ТФ-ПЕР-09): для носителя отдаются
/// только открытые дела с основанием поиска (действующее основание либо задание по объекту) и действующими эталонами
/// с лицом; заменённые эталоны и лица только с отозванных появлений не используются. Реальный Postgres.
/// </summary>
public sealed class InvestigationSuggestionTargetsTests : IAsyncLifetime
{
    private const int AssetId = 100;

    private readonly PostgreSqlContainer _postgres = TestPostgres.Create();

    public Task InitializeAsync() => _postgres.StartAsync();

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    [Fact(DisplayName = "Цели предложений: открытые дела с основанием и эталонами; основание — действующее последнее либо «задание № …»; лишние эталоны отброшены")]
    public async Task Targets_follow_basis_and_reference_rules()
    {
        var factory = new InvestigationContextFactory(_postgres.GetConnectionString());
        var core = new CoreContextFactory(_postgres.GetConnectionString());
        await InvestigationTestKit.MigrateAsync(factory);
        await InvestigationTestKit.AssignRolesAsync(factory, (10, InvestigationRole.Investigator));

        var cases = InvestigationTestKit.CreateCaseStore(factory, core);
        var persons = InvestigationTestKit.CreatePersonStore(factory, core);
        var scope = InvestigationTestKit.CreateCaseScope(factory, core);
        var owner = InvestigationTestKit.Access(10, 4, 5);

        // A — уголовное дело: два основания (истёкшее новее действующего), эталоны с разными судьбами.
        var caseA = (await cases.CreateAsync(InvestigationTestKit.Draft("A-1", 5, 2, 10), owner)).CaseId;
        await cases.AddAuthorizationAsync(new SearchAuthorizationDraft(
            caseA, AuthorizationKind.Resolution, "Постановление № 1", new DateOnly(2026, 9, 1), 10, null, null), owner);
        await cases.AddAuthorizationAsync(new SearchAuthorizationDraft(
            caseA, AuthorizationKind.Resolution, "Постановление № 2 (истекло)", new DateOnly(2026, 9, 10), 10, new DateOnly(2026, 9, 11), null), owner);

        var active = (await persons.CreateAsync(new PersonDraft(caseA, "Иванов", false, null, null, PersonRole.Target), owner)).PersonId;
        var old = await persons.AddReferencePhotoAsync(new ReferencePhotoDraft(active, 50, 999, 0.9f, null, null, null, null), null, owner);
        await persons.AddReferencePhotoAsync(new ReferencePhotoDraft(active, 51, 1000, 0.9f, null, null, null, null), old.PhotoId, owner);

        var noFace = (await persons.CreateAsync(new PersonDraft(caseA, "Петров", false, null, null), owner)).PersonId;
        await persons.AddReferencePhotoAsync(new ReferencePhotoDraft(noFace, 52, null, null, null, null, null, null), null, owner);

        var revoked = (await persons.CreateAsync(new PersonDraft(caseA, "Сидоров", false, null, null), owner)).PersonId;
        await persons.AddReferencePhotoAsync(new ReferencePhotoDraft(revoked, 53, 1001, 0.9f, null, null, null, null), null, owner);
        await persons.AddAppearanceAsync(new AppearanceDraft(revoked, caseA, 53, 1001, null, null, 7, 71, 0.9,
            new DateTime(2026, 9, 20, 0, 0, 0, DateTimeKind.Utc), 40, 41, 2, 5));
        await using (var db = factory.CreateDbContext())
        {
            await db.Appearances.Where(a => a.PersonId == revoked)
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.RevokedAtUtc, DateTime.UtcNow));
        }

        // B — задание по объекту без отдельного основания: основанием служит само задание.
        var references = new ReferenceStore(factory);
        var (_, unit) = await references.CreateAsync(ReferenceKind.InitiatorUnit, "ГУ-1", null, 0);
        var caseB = (await cases.CreateAsync(InvestigationTestKit.Draft("Т-1", 5, 2, 10) with
        {
            Kind = CaseKind.ObjectTask,
            TaskRequisites = new TaskRequisites("З-17/26", unit, "Обоснование", "Цель"),
        }, owner)).CaseId;
        var target = (await persons.CreateAsync(new PersonDraft(caseB, "Объект", false, null, null, PersonRole.Target), owner)).PersonId;
        await persons.AddReferencePhotoAsync(new ReferencePhotoDraft(target, 60, 2000, 0.9f, null, null, null, null), null, owner);

        // C — уголовное дело без основания: исключено. D — закрытое дело с основанием и эталоном: исключено.
        var caseC = (await cases.CreateAsync(InvestigationTestKit.Draft("C-1", 5, 2, 10), owner)).CaseId;
        var personC = (await persons.CreateAsync(new PersonDraft(caseC, "Без основания", false, null, null), owner)).PersonId;
        await persons.AddReferencePhotoAsync(new ReferencePhotoDraft(personC, 61, 3000, 0.9f, null, null, null, null), null, owner);

        var caseD = (await cases.CreateAsync(InvestigationTestKit.Draft("D-1", 5, 2, 10), owner)).CaseId;
        await cases.AddAuthorizationAsync(new SearchAuthorizationDraft(
            caseD, AuthorizationKind.Resolution, "Постановление № 9", new DateOnly(2026, 9, 1), 10, null, null), owner);
        var personD = (await persons.CreateAsync(new PersonDraft(caseD, "Закрытое", false, null, null), owner)).PersonId;
        await persons.AddReferencePhotoAsync(new ReferencePhotoDraft(personD, 62, 4000, 0.9f, null, null, null, null), null, owner);
        (await cases.SetStatusAsync(caseD, CaseStatus.Closed, owner)).ShouldBe(CaseWriteResult.Ok);

        foreach (var caseId in new[] { caseA, caseB, caseC, caseD })
        {
            (await cases.LinkMediaAsync(caseId, AssetId, null, 10)).ShouldBe(CaseWriteResult.Ok);
        }

        var targets = await scope.ListSuggestionTargetsAsync(AssetId);

        targets.Select(t => t.CaseId).ShouldBe([caseA, caseB]);

        var a = targets.Single(t => t.CaseId == caseA);
        a.AuthorizationRef.ShouldBe("Постановление № 1");
        a.Classification.ShouldBe((short)2);
        a.DivisionId.ShouldBe(5);
        a.References.ShouldHaveSingleItem().ShouldBe(new ISC.AI.Modules.Media.Domain.Services.SuggestionReference(active, 1000));

        var b = targets.Single(t => t.CaseId == caseB);
        b.AuthorizationRef.ShouldBe("задание № З-17/26");
        b.References.ShouldHaveSingleItem().ShouldBe(new ISC.AI.Modules.Media.Domain.Services.SuggestionReference(target, 2000));

        // Носитель без привязок — целей нет.
        (await scope.ListSuggestionTargetsAsync(AssetId + 1)).ShouldBeEmpty();

        // Одно дело — то же правило (сверка материалов дела при новом эталоне или основании, ADR-0035).
        (await scope.GetSuggestionTargetAsync(caseA)).ShouldNotBeNull().References
            .ShouldHaveSingleItem().ShouldBe(new ISC.AI.Modules.Media.Domain.Services.SuggestionReference(active, 1000));
        (await scope.GetSuggestionTargetAsync(caseB)).ShouldNotBeNull().AuthorizationRef.ShouldBe("задание № З-17/26");
        (await scope.GetSuggestionTargetAsync(caseC)).ShouldBeNull(); // нет основания
        (await scope.GetSuggestionTargetAsync(caseD)).ShouldBeNull(); // закрыто

        // Готовность для карточки носителя: доступные субъекту дела с причинами, без реквизитов основания.
        var readiness = await scope.ListSuggestionReadinessAsync(AssetId, owner);
        readiness.Select(r => r.CaseId).ShouldBe([caseA, caseB, caseC, caseD]);
        var readyA = readiness.Single(r => r.CaseId == caseA);
        readyA.CaseNumber.ShouldBe("A-1");
        (readyA.IsOpen, readyA.HasBasis, readyA.ReferenceCount).ShouldBe((true, true, 1));
        readyA.LatestReferenceAtUtc.ShouldNotBeNull().Kind.ShouldBe(DateTimeKind.Utc);
        var readyC = readiness.Single(r => r.CaseId == caseC);
        (readyC.IsOpen, readyC.HasBasis, readyC.ReferenceCount).ShouldBe((true, false, 1));
        readiness.Single(r => r.CaseId == caseD).IsOpen.ShouldBeFalse();

        // Дела выше допуска и субъект без роли — ничего (ТБ-012/021).
        (await scope.ListSuggestionReadinessAsync(AssetId, InvestigationTestKit.Access(10, 1, 5))).ShouldBeEmpty();
        (await scope.ListSuggestionReadinessAsync(AssetId, InvestigationTestKit.Access(99, 4, 5))).ShouldBeEmpty();
    }
}
