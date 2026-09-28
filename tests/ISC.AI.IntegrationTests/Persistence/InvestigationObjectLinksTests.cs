using System;
using System.Data.Common;
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
/// Связи объекта, адреса и автотранспорт (ТФ-ПЕР-06, ТО-мат-11) на настоящем Postgres: связь — фигурант того
/// же дела с типом из справочника; адреса и транспорт — под решёткой фигуранта с нормализованными значениями;
/// ограничения таблиц держат инварианты в обход хранилищ; уничтожение дела уносит всё; стартовое обслуживание
/// дозаполняет нормализованные поля прежних фигурантов.
/// </summary>
public sealed class InvestigationObjectLinksTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = TestPostgres.Create();

    public Task InitializeAsync() => _postgres.StartAsync();

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    [Fact(DisplayName = "Связь: фигурант того же дела с типом «тип связи»; другое дело, чужой вид справочника, выключенный тип, связь с собой и поля связи у иной роли → InvalidLink")]
    public async Task Links_are_checked_against_case_and_directory()
    {
        var kit = await ArrangeAsync();
        var owner = InvestigationTestKit.Access(10, 9, 5);
        var brother = await CreateItemAsync(kit.References, ReferenceKind.LinkType, "брат");
        var partner = await CreateItemAsync(kit.References, ReferenceKind.LinkType, "деловой партнёр");
        var rank = await CreateItemAsync(kit.References, ReferenceKind.Rank, "майор");

        var caseA = (await kit.Cases.CreateAsync(InvestigationTestKit.Draft("A-1", 5, 2, 10), owner)).CaseId;
        var caseB = (await kit.Cases.CreateAsync(InvestigationTestKit.Draft("B-1", 5, 2, 10), owner)).CaseId;
        var target = (await kit.Persons.CreateAsync(new PersonDraft(caseA, "Иванов Иван", false, null, null, PersonRole.Target), owner)).PersonId;
        var otherCaseTarget = (await kit.Persons.CreateAsync(new PersonDraft(caseB, "Сидоров", false, null, null, PersonRole.Target), owner)).PersonId;

        var link = await kit.Persons.CreateAsync(Link(caseA, "Иванов Пётр", target, brother), owner);
        link.Result.ShouldBe(PersonWriteResult.Ok);
        var row = (await kit.Persons.GetAsync(link.PersonId, owner)).ShouldNotBeNull();
        row.LinkedToPersonId.ShouldBe(target);
        row.LinkTypeId.ShouldBe(brother);

        // Фигурант другого дела, запись справочника другого вида, поля связи у иной роли.
        (await kit.Persons.CreateAsync(Link(caseA, "Петров", otherCaseTarget, brother), owner)).Result.ShouldBe(PersonWriteResult.InvalidLink);
        (await kit.Persons.CreateAsync(Link(caseA, "Петров", target, rank), owner)).Result.ShouldBe(PersonWriteResult.InvalidLink);
        (await kit.Persons.CreateAsync(Link(caseA, "Петров", target, brother) with { Role = PersonRole.Other }, owner))
            .Result.ShouldBe(PersonWriteResult.InvalidLink);

        // Связь с самим собой.
        (await kit.Persons.UpdateAsync(link.PersonId, Link(caseA, "Иванов Пётр", link.PersonId, brother), owner))
            .ShouldBe(PersonWriteResult.InvalidLink);

        // Выключенный тип: уже стоящий — правка проходит; новый выбор — отказ.
        (await kit.References.SetActiveAsync(brother, false)).ShouldBe(ReferenceWriteResult.Ok);
        (await kit.References.SetActiveAsync(partner, false)).ShouldBe(ReferenceWriteResult.Ok);
        (await kit.Persons.UpdateAsync(link.PersonId, Link(caseA, "Иванов Пётр Иванович", target, brother), owner))
            .ShouldBe(PersonWriteResult.Ok);
        (await kit.Persons.UpdateAsync(link.PersonId, Link(caseA, "Иванов Пётр Иванович", target, partner), owner))
            .ShouldBe(PersonWriteResult.InvalidLink);
        (await kit.Persons.CreateAsync(Link(caseA, "Петров", target, brother), owner)).Result.ShouldBe(PersonWriteResult.InvalidLink);

        // Смена роли на «иная» очищает поля связи.
        (await kit.Persons.UpdateAsync(link.PersonId, new PersonDraft(0, "Иванов Пётр", false, null, null, PersonRole.Other), owner))
            .ShouldBe(PersonWriteResult.Ok);
        var cleared = (await kit.Persons.GetAsync(link.PersonId, owner)).ShouldNotBeNull();
        cleared.LinkedToPersonId.ShouldBeNull();
        cleared.LinkTypeId.ShouldBeNull();

        // Чужому следователю связь не создать: дело недоступно — NotFound, а не InvalidLink (не оракул).
        (await kit.Persons.CreateAsync(Link(caseA, "Петров", target, null), InvestigationTestKit.Access(11, 9, 5)))
            .Result.ShouldBe(PersonWriteResult.NotFound);
    }

    [Fact(DisplayName = "Нормализация фигуранта: ФИО и место жительства пишутся ключом ТО-мат-11; у неустановленного ключа ФИО нет")]
    public async Task Person_name_and_residence_are_normalized()
    {
        var kit = await ArrangeAsync();
        var owner = InvestigationTestKit.Access(10, 9, 5);
        var caseA = (await kit.Cases.CreateAsync(InvestigationTestKit.Draft("A-1", 5, 2, 10), owner)).CaseId;

        var person = await kit.Persons.CreateAsync(new PersonDraft(caseA, "Семёнов Пётр", false, null, null, PersonRole.Target,
            new PersonQuestionnaire(Residence: "г. Бишкек, ул. Токтогула, д. 1")), owner);
        var unidentified = await kit.Persons.CreateAsync(new PersonDraft(caseA, null, true, null, null), owner);

        await using (var db = kit.Factory.CreateDbContext())
        {
            var stored = await db.Persons.AsNoTracking().SingleAsync(p => p.Id == person.PersonId);
            stored.NameNormalized.ShouldBe("петр семенов");
            stored.ResidenceNormalized.ShouldBe("бишкек ул токтогула 1");
            (await db.Persons.AsNoTracking().SingleAsync(p => p.Id == unidentified.PersonId)).NameNormalized.ShouldBeNull();
        }

        // Правка пересчитывает ключ.
        (await kit.Persons.UpdateAsync(person.PersonId, new PersonDraft(0, "Семенов Пётр Иванович", false, null, null, PersonRole.Target), owner))
            .ShouldBe(PersonWriteResult.Ok);
        await using (var db = kit.Factory.CreateDbContext())
        {
            var stored = await db.Persons.AsNoTracking().SingleAsync(p => p.Id == person.PersonId);
            stored.NameNormalized.ShouldBe("иванович петр семенов");
            stored.ResidenceNormalized.ShouldBeNull();
        }
    }

    [Fact(DisplayName = "Адреса и транспорт: запись, правка, удаление с нормализацией и грифом фигуранта; чужой субъект и чужая строка неотличимы от отсутствующих")]
    public async Task Requisites_round_trip_under_lattice()
    {
        var kit = await ArrangeAsync();
        var owner = InvestigationTestKit.Access(10, 9, 5);
        var stranger = InvestigationTestKit.Access(11, 9, 5);
        var caseA = (await kit.Cases.CreateAsync(InvestigationTestKit.Draft("A-1", 5, 3, 10), owner)).CaseId;
        var person = (await kit.Persons.CreateAsync(new PersonDraft(caseA, "Иванов", false, null, null, PersonRole.Target), owner)).PersonId;
        var other = (await kit.Persons.CreateAsync(new PersonDraft(caseA, "Петров", false, null, null), owner)).PersonId;

        var address = await kit.Requisites.SaveAddressAsync(person, null,
            new PersonAddressDraft(AddressKind.Residence, " г. Бишкек, ул. Токтогула, 1 ", "с 2025"), owner);
        address.Result.ShouldBe(PersonWriteResult.Ok);
        var vehicle = await kit.Requisites.SaveVehicleAsync(person, null,
            new PersonVehicleDraft(" 01 kg 123 авс ", "Toyota", "Camry", "белый", null), owner);
        vehicle.Result.ShouldBe(PersonWriteResult.Ok);

        var requisites = (await kit.Requisites.GetAsync(person, owner)).ShouldNotBeNull();
        requisites.Addresses.ShouldHaveSingleItem().Text.ShouldBe("г. Бишкек, ул. Токтогула, 1");
        requisites.Vehicles.ShouldHaveSingleItem().PlateNumber.ShouldBe("01 kg 123 авс");

        await using (var db = kit.Factory.CreateDbContext())
        {
            var storedAddress = await db.PersonAddresses.AsNoTracking().SingleAsync();
            storedAddress.TextNormalized.ShouldBe("бишкек ул токтогула 1");
            storedAddress.Classification.ShouldBe<short>(3);
            storedAddress.DivisionId.ShouldBe(5);
            var storedVehicle = await db.PersonVehicles.AsNoTracking().SingleAsync();
            storedVehicle.PlateNormalized.ShouldBe("01KG123ABC");
            storedVehicle.Classification.ShouldBe<short>(3);
        }

        // Правка пересчитывает ключ; правка «через чужого фигуранта» неотличима от отсутствия строки.
        (await kit.Requisites.SaveVehicleAsync(person, vehicle.VehicleId, new PersonVehicleDraft("01 KG 777 AAA", "Toyota", null, null, null), owner))
            .Result.ShouldBe(PersonWriteResult.Ok);
        (await kit.Requisites.SaveVehicleAsync(other, vehicle.VehicleId, new PersonVehicleDraft("X", null, null, null, null), owner))
            .Result.ShouldBe(PersonWriteResult.NotFound);
        await using (var db = kit.Factory.CreateDbContext())
        {
            (await db.PersonVehicles.AsNoTracking().SingleAsync()).PlateNormalized.ShouldBe("01KG777AAA");
        }

        // Чужой следователь: не видит, не пишет, не удаляет.
        (await kit.Requisites.GetAsync(person, stranger)).ShouldBeNull();
        (await kit.Requisites.SaveAddressAsync(person, null, new PersonAddressDraft(AddressKind.Stay, "Ош", null), stranger))
            .Result.ShouldBe(PersonWriteResult.NotFound);
        (await kit.Requisites.DeleteAddressAsync(address.AddressId, stranger)).ShouldBe(PersonWriteResult.NotFound);
        (await kit.Requisites.DeleteVehicleAsync(vehicle.VehicleId, stranger)).ShouldBe(PersonWriteResult.NotFound);

        // Допуск ниже грифа — тоже не видно (floor ядра).
        (await kit.Requisites.GetAsync(person, InvestigationTestKit.Access(10, 2, 5))).ShouldBeNull();

        (await kit.Requisites.DeleteAddressAsync(address.AddressId, owner)).ShouldBe(PersonWriteResult.Ok);
        (await kit.Requisites.DeleteVehicleAsync(vehicle.VehicleId, owner)).ShouldBe(PersonWriteResult.Ok);
        var empty = (await kit.Requisites.GetAsync(person, owner)).ShouldNotBeNull();
        empty.Addresses.ShouldBeEmpty();
        empty.Vehicles.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Ограничения таблиц: поля связи у иной роли, связь с собой, транспорт без номера и марки — не записать и в обход хранилища; уничтожение дела уносит связи, адреса и транспорт")]
    public async Task Table_constraints_and_purge()
    {
        var kit = await ArrangeAsync();
        var owner = InvestigationTestKit.Access(10, 9, 5);
        var linkType = await CreateItemAsync(kit.References, ReferenceKind.LinkType, "брат");
        var caseA = (await kit.Cases.CreateAsync(InvestigationTestKit.Draft("A-1", 5, 2, 10), owner)).CaseId;
        var target = (await kit.Persons.CreateAsync(new PersonDraft(caseA, "Иванов", false, null, null, PersonRole.Target), owner)).PersonId;
        var link = (await kit.Persons.CreateAsync(Link(caseA, "Иванов Пётр", target, linkType), owner)).PersonId;
        (await kit.Requisites.SaveAddressAsync(link, null, new PersonAddressDraft(AddressKind.Residence, "Ош", null), owner))
            .Result.ShouldBe(PersonWriteResult.Ok);
        (await kit.Requisites.SaveVehicleAsync(target, null, new PersonVehicleDraft("01 KG 123 ABC", null, null, null, null), owner))
            .Result.ShouldBe(PersonWriteResult.Ok);

        await using (var db = kit.Factory.CreateDbContext())
        {
            var linkOnTarget = async () => await db.Database.ExecuteSqlAsync(
                $"UPDATE investigation.person SET linked_to_person_id = {link} WHERE id = {target}");
            await linkOnTarget.ShouldThrowAsync<DbException>();
            var selfLink = async () => await db.Database.ExecuteSqlAsync(
                $"UPDATE investigation.person SET linked_to_person_id = {link} WHERE id = {link}");
            await selfLink.ShouldThrowAsync<DbException>();
            var emptyVehicle = async () => await db.Database.ExecuteSqlAsync(
                $"UPDATE investigation.person_vehicle SET plate_number = ' ', make = NULL WHERE person_id = {target}");
            await emptyVehicle.ShouldThrowAsync<DbException>();
        }

        // Уничтожение дела (ADR-0025): каскад уносит фигурантов с их адресами и транспортом, связь с «объектом»
        // внутри того же удаления не блокирует его.
        (await kit.Cases.PurgeAsync(caseA, owner)).ShouldBe(CaseWriteResult.Ok);
        await using (var db = kit.Factory.CreateDbContext())
        {
            (await db.Persons.CountAsync()).ShouldBe(0);
            (await db.PersonAddresses.CountAsync()).ShouldBe(0);
            (await db.PersonVehicles.CountAsync()).ShouldBe(0);
        }
    }

    [Fact(DisplayName = "Обслуживание при старте: пустые ключи ФИО и места жительства прежних фигурантов дозаполняются тем же кодом; повторный запуск ничего не меняет")]
    public async Task Startup_backfill_fills_missing_keys_once()
    {
        var kit = await ArrangeAsync();
        var owner = InvestigationTestKit.Access(10, 9, 5);
        var caseA = (await kit.Cases.CreateAsync(InvestigationTestKit.Draft("A-1", 5, 2, 10), owner)).CaseId;
        var person = (await kit.Persons.CreateAsync(new PersonDraft(caseA, "Семёнов Пётр", false, null, null, PersonRole.Target,
            new PersonQuestionnaire(Residence: "город Ош")), owner)).PersonId;
        var unidentified = (await kit.Persons.CreateAsync(new PersonDraft(caseA, null, true, null, null), owner)).PersonId;

        // Как у фигурантов, заведённых до появления ключей.
        await using (var db = kit.Factory.CreateDbContext())
        {
            await db.Database.ExecuteSqlRawAsync("UPDATE investigation.person SET name_normalized = NULL, residence_normalized = NULL");
        }

        (await InvestigationStartupMaintenance.BackfillNormalizedAsync(kit.Factory)).ShouldBe(1);
        (await InvestigationStartupMaintenance.BackfillNormalizedAsync(kit.Factory)).ShouldBe(0);

        await using (var db = kit.Factory.CreateDbContext())
        {
            var stored = await db.Persons.AsNoTracking().SingleAsync(p => p.Id == person);
            stored.NameNormalized.ShouldBe("петр семенов");
            stored.ResidenceNormalized.ShouldBe("ош");
            (await db.Persons.AsNoTracking().SingleAsync(p => p.Id == unidentified)).NameNormalized.ShouldBeNull();
        }
    }

    private async Task<Kit> ArrangeAsync()
    {
        var factory = new InvestigationContextFactory(_postgres.GetConnectionString());
        var core = new CoreContextFactory(_postgres.GetConnectionString());
        await InvestigationTestKit.MigrateAsync(factory);
        await InvestigationTestKit.AssignRolesAsync(factory, (10, InvestigationRole.Investigator), (11, InvestigationRole.Investigator));
        return new Kit(
            factory,
            InvestigationTestKit.CreateCaseStore(factory, core),
            InvestigationTestKit.CreatePersonStore(factory, core),
            new PersonRequisiteStore(factory, new InvestigationAccessPolicy(factory), new UserRoleStore(core, factory)),
            new ReferenceStore(factory));
    }

    private static async Task<int> CreateItemAsync(ReferenceStore references, ReferenceKind kind, string name)
    {
        var (result, id) = await references.CreateAsync(kind, name, null, 0);
        result.ShouldBe(ReferenceWriteResult.Ok);
        return id;
    }

    private static PersonDraft Link(int caseId, string name, int? linkedTo, int? linkType) =>
        new(caseId, name, false, null, null, PersonRole.Link, null, linkedTo, linkType);

    private sealed record Kit(
        InvestigationContextFactory Factory,
        CaseStore Cases,
        PersonStore Persons,
        PersonRequisiteStore Requisites,
        ReferenceStore References);
}
