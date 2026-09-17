using System;
using System.Linq;
using System.Threading.Tasks;
using ISC.AI.Profile.Investigation.Domain.Enums;
using ISC.AI.Profile.Investigation.Domain.Services;
using Shouldly;
using Testcontainers.PostgreSql;

namespace ISC.AI.IntegrationTests.Persistence;

/// <summary>
/// Фигуранты, эталоны и появления (ТФ-ПЕР-01/02, ТБ-070/073/077) через <c>PersonStore</c>: решётка через
/// дело, нумерация неустановленных лиц, смена эталона с сохранением прежнего, появление всегда
/// «следственная версия». Реальный Postgres через Testcontainers.
/// </summary>
public sealed class InvestigationPersonStoreTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = TestPostgres.Create();

    public Task InitializeAsync() => _postgres.StartAsync();

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    [Fact(DisplayName = "Фигуранты под решёткой дела: гриф/подразделение копируются с дела; чужое дело → NotFound; нумерация «Неустановленное лицо № N»")]
    public async Task Persons_follow_case_lattice_and_unidentified_numbering()
    {
        var factory = new InvestigationContextFactory(_postgres.GetConnectionString());
        var core = new CoreContextFactory(_postgres.GetConnectionString());
        await InvestigationTestKit.MigrateAsync(factory);
        await InvestigationTestKit.AssignRolesAsync(factory,
            (10, InvestigationRole.Investigator),
            (11, InvestigationRole.Investigator),
            (20, InvestigationRole.Head));

        var cases = InvestigationTestKit.CreateCaseStore(factory, core);
        var persons = InvestigationTestKit.CreatePersonStore(factory, core);

        var head = InvestigationTestKit.Access(20, 9, 5);
        var caseA = await cases.CreateAsync(InvestigationTestKit.Draft("A-1", 5, 2, 10), head);
        var caseB = await cases.CreateAsync(InvestigationTestKit.Draft("B-1", 5, 2, 11), head);
        caseA.Result.ShouldBe(CaseWriteResult.Ok);
        caseB.Result.ShouldBe(CaseWriteResult.Ok);

        var owner = InvestigationTestKit.Access(10, 9, 5);

        var p1 = await persons.CreateAsync(new PersonDraft(caseA.CaseId, null, IsUnidentified: true, "подозреваемый", null), owner);
        var p2 = await persons.CreateAsync(new PersonDraft(caseA.CaseId, "  ", IsUnidentified: true, null, null), owner);
        var p3 = await persons.CreateAsync(new PersonDraft(caseA.CaseId, "Иванов И. И.", IsUnidentified: false, "свидетель", "примечание"), owner);
        p1.Result.ShouldBe(PersonWriteResult.Ok);
        p2.Result.ShouldBe(PersonWriteResult.Ok);
        p3.Result.ShouldBe(PersonWriteResult.Ok);

        var rows = await persons.ListByCaseAsync(caseA.CaseId, owner);
        rows.Count.ShouldBe(3);
        rows[0].DisplayName.ShouldBe("Неустановленное лицо № 1");
        rows[0].UnidentifiedNumber.ShouldBe(1);
        rows[1].DisplayName.ShouldBe("Неустановленное лицо № 2");
        rows[1].UnidentifiedNumber.ShouldBe(2);
        rows[2].DisplayName.ShouldBe("Иванов И. И.");
        rows[2].IsUnidentified.ShouldBeFalse();
        rows[2].UnidentifiedNumber.ShouldBeNull();

        // ТБ-070: режимные поля — с дела.
        rows.ShouldAllBe(r => r.Classification == 2 && r.DivisionId == 5);

        // Чужое дело (следователь 11 в деле A) — фигуранта не создать и не увидеть.
        var stranger = InvestigationTestKit.Access(11, 9, 5);
        (await persons.CreateAsync(new PersonDraft(caseA.CaseId, "Петров", false, null, null), stranger)).Result
            .ShouldBe(PersonWriteResult.NotFound);
        (await persons.ListByCaseAsync(caseA.CaseId, stranger)).ShouldBeEmpty();
        (await persons.GetAsync(p3.PersonId, stranger)).ShouldBeNull();

        // Floor ядра: тот же следователь, но допуск ниже грифа дела — фигурантов не видно.
        (await persons.GetAsync(p3.PersonId, InvestigationTestKit.Access(10, 1, 5))).ShouldBeNull();
        (await persons.GetAsync(p3.PersonId, InvestigationTestKit.Access(10, 9, 6))).ShouldBeNull();

        // Установление личности сохраняет номер; пустое имя у установленного — ошибка вызывающего.
        (await persons.UpdateAsync(p1.PersonId, "Сидоров", false, "подозреваемый", null, owner)).ShouldBe(PersonWriteResult.Ok);
        var updated = (await persons.GetAsync(p1.PersonId, owner)).ShouldNotBeNull();
        updated.DisplayName.ShouldBe("Сидоров");
        updated.IsUnidentified.ShouldBeFalse();
        updated.UnidentifiedNumber.ShouldBe(1);

        await Should.ThrowAsync<ArgumentException>(
            () => persons.UpdateAsync(p3.PersonId, "", false, null, null, owner));
    }

    [Fact(DisplayName = "Смена эталона: прежний помечается SupersededById и НЕ удаляется (ТБ-077); гриф эталона — с фигуранта")]
    public async Task Reference_photo_replacement_keeps_previous()
    {
        var factory = new InvestigationContextFactory(_postgres.GetConnectionString());
        var core = new CoreContextFactory(_postgres.GetConnectionString());
        await InvestigationTestKit.MigrateAsync(factory);
        await InvestigationTestKit.AssignRolesAsync(factory, (10, InvestigationRole.Investigator));

        var cases = InvestigationTestKit.CreateCaseStore(factory, core);
        var persons = InvestigationTestKit.CreatePersonStore(factory, core);
        var owner = InvestigationTestKit.Access(10, 9, 5);

        var caseA = await cases.CreateAsync(InvestigationTestKit.Draft("A-1", 5, 3, 10), owner);
        var person = await persons.CreateAsync(new PersonDraft(caseA.CaseId, "Иванов", false, null, null), owner);

        var first = await persons.AddReferencePhotoAsync(
            new ReferencePhotoDraft(person.PersonId, MediaAssetId: 100, MediaFaceId: 1000, 0.9f, "паспорт", "постановление № 1", null, null),
            supersedesId: null, owner);
        first.Result.ShouldBe(PersonWriteResult.Ok);

        var second = await persons.AddReferencePhotoAsync(
            new ReferencePhotoDraft(person.PersonId, MediaAssetId: 101, MediaFaceId: 1001, 0.95f, "видео", "постановление № 2", new DateOnly(2027, 1, 1), null),
            supersedesId: first.PhotoId, owner);
        second.Result.ShouldBe(PersonWriteResult.Ok);

        var photos = await persons.ListReferencePhotosAsync(person.PersonId, owner);
        photos.Count.ShouldBe(2);
        photos.Single(p => p.Id == first.PhotoId).SupersededById.ShouldBe(second.PhotoId);
        photos.Single(p => p.Id == second.PhotoId).SupersededById.ShouldBeNull();
        photos.Single(p => p.Id == second.PhotoId).AddedByUserId.ShouldBe(10);

        // Чужой эталон в supersedesId — NotFound, ничего не записано.
        (await persons.AddReferencePhotoAsync(
            new ReferencePhotoDraft(person.PersonId, 102, null, null, null, null, null, null),
            supersedesId: 999_999, owner)).Result.ShouldBe(PersonWriteResult.NotFound);
        (await persons.ListReferencePhotosAsync(person.PersonId, owner)).Count.ShouldBe(2);

        // Гриф эталона — с фигуранта (= дела): 3.
        await using var db = factory.CreateDbContext();
        var stored = db.ReferencePhotos.Single(p => p.Id == second.PhotoId);
        stored.Classification.ShouldBe<short>(3);
        stored.DivisionId.ShouldBe(5);
        (await persons.GetAsync(person.PersonId, owner)).ShouldNotBeNull().ReferencePhotoCount.ShouldBe(2);
    }

    [Fact(DisplayName = "Появление: всегда «следственная версия»; эксперт = верификатор → отказ (ТБ-073); новые первыми")]
    public async Task Appearances_are_investigative_leads_and_require_two_persons()
    {
        var factory = new InvestigationContextFactory(_postgres.GetConnectionString());
        var core = new CoreContextFactory(_postgres.GetConnectionString());
        await InvestigationTestKit.MigrateAsync(factory);
        await InvestigationTestKit.AssignRolesAsync(factory, (10, InvestigationRole.Investigator));

        var cases = InvestigationTestKit.CreateCaseStore(factory, core);
        var persons = InvestigationTestKit.CreatePersonStore(factory, core);
        var owner = InvestigationTestKit.Access(10, 9, 5);

        var caseA = await cases.CreateAsync(InvestigationTestKit.Draft("A-1", 5, 1, 10), owner);
        var person = await persons.CreateAsync(new PersonDraft(caseA.CaseId, "Иванов", false, null, null), owner);

        var older = new AppearanceDraft(person.PersonId, caseA.CaseId, 100, 1000, null, null, 7, 71, 0.81,
            new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc), ExpertUserId: 40, VerifierUserId: 41, 1, 5);
        var newer = older with { CandidateId = 72, MediaFaceId = 1001, FrameIndex = 12, FrameTimestampMs = 480,
            ConfirmedAtUtc = new DateTime(2026, 9, 2, 10, 0, 0, DateTimeKind.Utc) };

        await persons.AddAppearanceAsync(older);
        await persons.AddAppearanceAsync(newer);

        // Самоподтверждение отклоняется на границе хранилища (ТБ-073).
        await Should.ThrowAsync<InvalidOperationException>(
            () => persons.AddAppearanceAsync(older with { CandidateId = 73, VerifierUserId = 40 }));

        var list = await persons.ListAppearancesAsync(person.PersonId, owner);
        list.Count.ShouldBe(2);
        list[0].CandidateId.ShouldBe(72);
        list[0].FrameTimestampMs.ShouldBe(480);
        list[1].CandidateId.ShouldBe(71);
        list.ShouldAllBe(a => a.Status == AppearanceStatus.InvestigativeLead);
        list.ShouldAllBe(a => a.ExpertUserId != a.VerifierUserId);

        // Один кандидат — одно появление (уникальный индекс).
        await Should.ThrowAsync<Microsoft.EntityFrameworkCore.DbUpdateException>(
            () => persons.AddAppearanceAsync(newer));

        // Чужому субъекту появления не видны.
        (await persons.ListAppearancesAsync(person.PersonId, InvestigationTestKit.Access(11, 9, 5))).ShouldBeEmpty();
        (await persons.GetAsync(person.PersonId, owner)).ShouldNotBeNull().AppearanceCount.ShouldBe(2);
    }
}
