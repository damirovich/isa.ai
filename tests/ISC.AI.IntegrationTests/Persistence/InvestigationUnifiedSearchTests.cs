using System;
using System.Linq;
using System.Threading.Tasks;
using ISC.AI.Profile.Investigation.Data;
using ISC.AI.Profile.Investigation.Domain.Enums;
using ISC.AI.Profile.Investigation.Domain.Services;
using Shouldly;
using Testcontainers.PostgreSql;

namespace ISC.AI.IntegrationTests.Persistence;

/// <summary>
/// Единый поиск в хранилищах профиля (ТФ-ПСК-01, ADR-0048) на настоящем Postgres: фигуранты по ФИО (с «ё»), псевдониму,
/// дате и году рождения; автотранспорт по госномеру в любом написании (кириллица, пробелы) и по марке; адреса с «улица»
/// и «ул.» — строкой адреса и из анкеты; сводки по тексту бланка; привязки документов и носителей к делам. Область —
/// переданные дела, но решётка применяется заново: чужое дело, даже переданное в области, ничего не выдаёт (ТБ-020/071).
/// </summary>
[Trait("Category", "Gate")]
public sealed class InvestigationUnifiedSearchTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = TestPostgres.Create();

    public Task InitializeAsync() => _postgres.StartAsync();

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    [Fact(DisplayName = "Поиск в хранилищах: ФИО/псевдоним/дата рождения, госномер в любом написании, адрес «улица»/«ул.», сводки, документы и носители — только в своих делах")]
    public async Task Stores_search_within_scope_and_lattice()
    {
        var factory = new InvestigationContextFactory(_postgres.GetConnectionString());
        var core = new CoreContextFactory(_postgres.GetConnectionString());
        await InvestigationTestKit.MigrateAsync(factory);
        await InvestigationTestKit.AssignRolesAsync(factory, (10, InvestigationRole.Investigator), (11, InvestigationRole.Investigator));
        var policy = new InvestigationAccessPolicy(factory);
        var roles = new UserRoleStore(core, factory);
        var cases = InvestigationTestKit.CreateCaseStore(factory, core);
        var persons = InvestigationTestKit.CreatePersonStore(factory, core);
        var requisites = new PersonRequisiteStore(factory, policy, roles);
        var reports = new CaseReportStore(factory, policy, roles, new CaseReportOptions(), TimeProvider.System);
        var documents = new CaseDocumentStore(factory, policy, roles);

        // Следователь 10 ведёт А и Б; следователь 11 — В (то же подразделение и допуск, но не его дело).
        var mine = InvestigationTestKit.Access(10, 9, 5);
        var other = InvestigationTestKit.Access(11, 9, 5);
        var caseA = (await cases.CreateAsync(InvestigationTestKit.Draft("А-1", 5, 1, 10), mine)).CaseId;
        var caseB = (await cases.CreateAsync(InvestigationTestKit.Draft("Б-2", 5, 1, 10), mine)).CaseId;
        var caseC = (await cases.CreateAsync(InvestigationTestKit.Draft("В-3", 5, 1, 11), other)).CaseId;
        int[] all = [caseA, caseB, caseC];

        var petr = (await persons.CreateAsync(new PersonDraft(caseA, "Иванов Пётр", false, null, null, PersonRole.Target,
            new PersonQuestionnaire(BirthDate: new DateOnly(1985, 3, 12), Alias: "Шрам")), mine)).PersonId;
        var oleg = (await persons.CreateAsync(new PersonDraft(caseB, "Сидоров Олег", false, null, null, PersonRole.Target,
            new PersonQuestionnaire(BirthYear: 1990, Residence: "г. Бишкек, улица Ленина 5")), mine)).PersonId;
        var semen = (await persons.CreateAsync(new PersonDraft(caseC, "Иванов Семён", false, null, null, PersonRole.Target), other)).PersonId;

        (await requisites.SaveVehicleAsync(petr, null, new PersonVehicleDraft("01 KG 123 abc", "Toyota", "Camry", "белый", null), mine))
            .Result.ShouldBe(PersonWriteResult.Ok);
        (await requisites.SaveVehicleAsync(semen, null, new PersonVehicleDraft("01KG123ABC", "Honda", null, null, null), other))
            .Result.ShouldBe(PersonWriteResult.Ok);
        (await requisites.SaveAddressAsync(oleg, null, new PersonAddressDraft(AddressKind.Residence, "ул. Ленина, д. 5", null), mine))
            .Result.ShouldBe(PersonWriteResult.Ok);
        (await requisites.SaveAddressAsync(semen, null, new PersonAddressDraft(AddressKind.Work, "улица Ленина 5", null), other))
            .Result.ShouldBe(PersonWriteResult.Ok);

        (await reports.CreateAsync(new CaseReportDraft(caseA, CaseReportKind.SummaryOn, new DateOnly(2026, 10, 1), petr,
            new CaseReportContent(Conclusion: "Встреча у рынка Ош")), mine)).Result.ShouldBe(CaseReportWriteResult.Ok);
        (await reports.CreateAsync(new CaseReportDraft(caseC, CaseReportKind.SummaryOn, new DateOnly(2026, 10, 2), semen,
            new CaseReportContent(Conclusion: "Тоже у рынка")), other)).Result.ShouldBe(CaseReportWriteResult.Ok);

        // --- фигуранты: ФИО с «ё», псевдоним, дата и год рождения; чужое дело В не выдаётся, хоть и передано ---
        (await persons.SearchInCasesAsync(SearchTerms.Parse("ИВАНОВ ПЕТР"), all, 10, mine)).Select(p => p.Id).ShouldBe([petr]);
        (await persons.SearchInCasesAsync(SearchTerms.Parse("иванов"), all, 10, mine)).Select(p => p.Id).ShouldBe([petr]);
        (await persons.SearchInCasesAsync(SearchTerms.Parse("шрам"), all, 10, mine)).Select(p => p.Id).ShouldBe([petr]);
        (await persons.SearchInCasesAsync(SearchTerms.Parse("12.03.1985"), all, 10, mine)).Select(p => p.Id).ShouldBe([petr]);
        (await persons.SearchInCasesAsync(SearchTerms.Parse("1985"), all, 10, mine)).Select(p => p.Id).ShouldBe([petr]);
        (await persons.SearchInCasesAsync(SearchTerms.Parse("1990"), all, 10, mine)).Select(p => p.Id).ShouldBe([oleg]);
        (await persons.SearchInCasesAsync(SearchTerms.Parse("иванов"), [caseB], 10, mine)).ShouldBeEmpty(); // вне области
        (await persons.SearchInCasesAsync(SearchTerms.Parse("иванов"), all, 10, other)).Select(p => p.Id).ShouldBe([semen]);

        // --- автотранспорт: номер кириллицей и с пробелами находит латинский; марка; чужой — нет ---
        var byPlate = await requisites.SearchVehiclesAsync(SearchTerms.Parse("123 авс"), all, 10, mine);
        byPlate.Select(v => (v.PersonId, v.CaseId, v.Vehicle.PlateNumber)).ShouldBe([(petr, caseA, "01 KG 123 abc")]);
        (await requisites.SearchVehiclesAsync(SearchTerms.Parse("toyota"), all, 10, mine)).Count.ShouldBe(1);
        (await requisites.SearchVehiclesAsync(SearchTerms.Parse("honda"), all, 10, mine)).ShouldBeEmpty();

        // --- адреса: «улица» = «ул.», строка адреса и место жительства из анкеты; адрес чужого фигуранта — нет ---
        var addresses = await requisites.SearchAddressesAsync(SearchTerms.Parse("улица Ленина"), all, 10, mine);
        addresses.Select(a => (a.PersonId, a.Kind)).ShouldBe([(oleg, (AddressKind?)AddressKind.Residence), (oleg, (AddressKind?)null)]);
        addresses[1].Text.ShouldBe("г. Бишкек, улица Ленина 5");

        // --- сводки: текст бланка; чужая сводка — нет ---
        var found = await reports.SearchAsync(SearchTerms.Parse("рынка"), all, 10, mine);
        found.Select(r => r.CaseId).ShouldBe([caseA]);
        found[0].PersonName.ShouldBe("Иванов Пётр");

        // --- документы дел: привязки только доступных дел ---
        (await documents.AttachAsync(caseA, 101, mine)).ShouldBe(CaseDocumentWriteResult.Ok);
        (await documents.AttachAsync(caseC, 102, other)).ShouldBe(CaseDocumentWriteResult.Ok);
        (await documents.ListLinksAsync(all, mine)).ShouldBe([new CaseDocumentRef(caseA, 101)]);

        // --- носители: дело каждого носителя среди дел области, первая привязка ---
        (await cases.LinkMediaAsync(caseA, 500, null, 10)).ShouldBe(CaseWriteResult.Ok);
        (await cases.LinkMediaAsync(caseB, 500, null, 10)).ShouldBe(CaseWriteResult.Ok);
        (await cases.LinkMediaAsync(caseB, 501, null, 10)).ShouldBe(CaseWriteResult.Ok);
        var owners = await cases.MapMediaAssetsAsync([500, 501, 502], [caseA, caseB]);
        owners.ShouldBe(new System.Collections.Generic.Dictionary<int, int> { [500] = caseA, [501] = caseB }, ignoreOrder: true);
        (await cases.MapMediaAssetsAsync([500], [caseB]))[500].ShouldBe(caseB);
    }

    [Fact(DisplayName = "ADR-0055: телефон — в любом написании и частью от 5 цифр, только в своих делах; гражданство и ориентировка анкеты — сохраняются, гражданство ищется")]
    public async Task Phones_and_citizenship_are_searched_within_scope()
    {
        var factory = new InvestigationContextFactory(_postgres.GetConnectionString());
        var core = new CoreContextFactory(_postgres.GetConnectionString());
        await InvestigationTestKit.MigrateAsync(factory);
        await InvestigationTestKit.AssignRolesAsync(factory, (10, InvestigationRole.Investigator), (11, InvestigationRole.Investigator));
        var cases = InvestigationTestKit.CreateCaseStore(factory, core);
        var persons = InvestigationTestKit.CreatePersonStore(factory, core);
        var requisites = new PersonRequisiteStore(factory, new InvestigationAccessPolicy(factory), new UserRoleStore(core, factory));

        var mine = InvestigationTestKit.Access(10, 9, 5);
        var other = InvestigationTestKit.Access(11, 9, 5);
        var caseA = (await cases.CreateAsync(InvestigationTestKit.Draft("А-1", 5, 1, 10), mine)).CaseId;
        var caseC = (await cases.CreateAsync(InvestigationTestKit.Draft("В-3", 5, 1, 11), other)).CaseId;
        int[] all = [caseA, caseC];

        var petr = (await persons.CreateAsync(new PersonDraft(caseA, "Иванов Пётр", false, null, null, PersonRole.Target,
            new PersonQuestionnaire(Citizenship: "Казахстан", Orientation: "Ездит на белой Camry")), mine)).PersonId;
        var semen = (await persons.CreateAsync(new PersonDraft(caseC, "Иванов Семён", false, null, null, PersonRole.Target,
            new PersonQuestionnaire(Citizenship: "Казахстан")), other)).PersonId;
        (await requisites.SavePhoneAsync(petr, null, new PersonPhoneDraft(PhoneKind.Mobile, "+996 555 12-34-56", null), mine))
            .Result.ShouldBe(PersonWriteResult.Ok);
        (await requisites.SavePhoneAsync(semen, null, new PersonPhoneDraft(PhoneKind.Mobile, "0555 123 456", null), other))
            .Result.ShouldBe(PersonWriteResult.Ok);

        // Анкета: новые поля пишутся и читаются как введены.
        var stored = (await persons.GetAsync(petr, mine)).ShouldNotBeNull().Questionnaire.ShouldNotBeNull();
        stored.Citizenship.ShouldBe("Казахстан");
        stored.Orientation.ShouldBe("Ездит на белой Camry");

        // Телефон: с нулём, с кодом, частью; чужой фигурант с тем же номером — нет, хоть его дело и передано в области.
        foreach (var query in new[] { "0555 123 456", "+996 (555) 123-456", "123456", "12-34-56" })
        {
            var found = await requisites.SearchPhonesAsync(SearchTerms.Parse(query), all, 10, mine);
            found.Select(p => (p.PersonId, p.CaseId, p.Phone.Number)).ShouldBe([(petr, caseA, "+996 555 12-34-56")], query);
        }

        (await requisites.SearchPhonesAsync(SearchTerms.Parse("0555 999 999"), all, 10, mine)).ShouldBeEmpty();
        (await requisites.SearchPhonesAsync(SearchTerms.Parse("Иванов"), all, 10, mine)).ShouldBeEmpty(); // не номер — номера не ищутся
        (await requisites.SearchPhonesAsync(SearchTerms.Parse("_5%"), all, 10, mine)).ShouldBeEmpty();
        (await requisites.SearchPhonesAsync(SearchTerms.Parse("0555 123 456"), all, 10, other)).Select(p => p.PersonId).ShouldBe([semen]);

        // Гражданство — часть текста поиска по фигурантам; чужое дело — нет.
        (await persons.SearchInCasesAsync(SearchTerms.Parse("казах"), all, 10, mine)).Select(p => p.Id).ShouldBe([petr]);
    }
}
