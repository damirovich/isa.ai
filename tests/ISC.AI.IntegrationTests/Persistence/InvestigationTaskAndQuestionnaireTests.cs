using System;
using System.Data.Common;
using System.Linq;
using System.Threading.Tasks;
using ISC.AI.Profile.Investigation.Data;
using ISC.AI.Profile.Investigation.Domain.Enums;
using ISC.AI.Profile.Investigation.Domain.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Shouldly;
using Testcontainers.PostgreSql;

namespace ISC.AI.IntegrationTests.Persistence;

/// <summary>
/// Задание по объекту и анкета (ТФ-ДЕЛ-05, ТФ-ПЕР-01/05, ТФ-АДМ-07) на настоящем Postgres: реквизиты задания
/// есть ровно у задания и ссылаются на действующие записи справочника своего вида; смена вида очищает их;
/// справочник уникален без учёта регистра и не удаляется, пока на запись ссылается дело; анкета хранит год
/// рождения согласованным с датой; ограничения таблиц держат инварианты и в обход хранилищ; миграция
/// проставляет прежним фигурантам роль «иная».
/// </summary>
public sealed class InvestigationTaskAndQuestionnaireTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = TestPostgres.Create();

    public Task InitializeAsync() => _postgres.StartAsync();

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    [Fact(DisplayName = "Задание: реквизиты сохраняются и читаются; в строке списка — № задания и ГУ; поиск по № задания находит дело")]
    public async Task Task_requisites_round_trip_and_are_searchable()
    {
        var (factory, cases, references) = await ArrangeAsync();
        var unit = await CreateItemAsync(references, ReferenceKind.InitiatorUnit, "ГУ по борьбе с оргпреступностью");
        var rank = await CreateItemAsync(references, ReferenceKind.Rank, "подполковник");
        var position = await CreateItemAsync(references, ReferenceKind.Position, "начальник отдела");
        var owner = InvestigationTestKit.Access(10, 9, 5);

        var task = new TaskRequisites(
            "  З-17/26 ", unit, "Проверка оперативной информации", "Установить связи объекта",
            "Асанов А. А.", rank, position, "+996 312 00-00-00", "каб. 305", "срочно");
        var created = await cases.CreateAsync(TaskDraft("Т-1", task), owner);
        created.Result.ShouldBe(CaseWriteResult.Ok);

        var details = (await cases.GetAsync(created.CaseId, owner)).ShouldNotBeNull();
        details.Kind.ShouldBe(CaseKind.ObjectTask);
        var stored = details.TaskRequisites.ShouldNotBeNull();
        stored.TaskNumber.ShouldBe("З-17/26");
        stored.InitiatorUnitId.ShouldBe(unit);
        stored.InitiatorRankId.ShouldBe(rank);
        stored.InitiatorPositionId.ShouldBe(position);
        stored.InitiatorName.ShouldBe("Асанов А. А.");
        stored.Justification.ShouldBe("Проверка оперативной информации");
        stored.Purpose.ShouldBe("Установить связи объекта");
        stored.Notes.ShouldBe("срочно");
        stored.InitiatorPhone.ShouldBe("+996 312 00-00-00");
        stored.InitiatorDetails.ShouldBe("каб. 305");

        var page = await cases.ListAsync(new CaseFilter(Text: "17/26"), owner);
        var row = page.Rows.ShouldHaveSingleItem();
        row.TaskNumber.ShouldBe("З-17/26");
        row.InitiatorUnitId.ShouldBe(unit);

        // Уголовное дело реквизитов задания не имеет.
        var criminal = await cases.CreateAsync(InvestigationTestKit.Draft("УД-1", 5, 2, 10), owner);
        (await cases.GetAsync(criminal.CaseId, owner)).ShouldNotBeNull().TaskRequisites.ShouldBeNull();

        await using var db = factory.CreateDbContext();
        (await db.Cases.SingleAsync(c => c.Id == criminal.CaseId)).TaskNumber.ShouldBeNull();
    }

    [Fact(DisplayName = "Задание: запись справочника чужого вида, несуществующая или выключенная → InvalidTask; реквизиты у уголовного дела и задание без реквизитов → InvalidTask")]
    public async Task Task_requisites_are_checked_against_kind_and_references()
    {
        var (_, cases, references) = await ArrangeAsync();
        var unit = await CreateItemAsync(references, ReferenceKind.InitiatorUnit, "ГУ-1");
        var rank = await CreateItemAsync(references, ReferenceKind.Rank, "майор");
        var retired = await CreateItemAsync(references, ReferenceKind.InitiatorUnit, "Расформированное ГУ");
        (await references.SetActiveAsync(retired, false)).ShouldBe(ReferenceWriteResult.Ok);
        var owner = InvestigationTestKit.Access(10, 9, 5);

        // Звание вместо ГУ, ГУ вместо звания, несуществующая запись, выключенный ГУ.
        (await cases.CreateAsync(TaskDraft("Т-1", Requisites(rank)), owner)).Result.ShouldBe(CaseWriteResult.InvalidTask);
        (await cases.CreateAsync(TaskDraft("Т-2", Requisites(unit) with { InitiatorRankId = unit }), owner)).Result.ShouldBe(CaseWriteResult.InvalidTask);
        (await cases.CreateAsync(TaskDraft("Т-3", Requisites(999_999)), owner)).Result.ShouldBe(CaseWriteResult.InvalidTask);
        (await cases.CreateAsync(TaskDraft("Т-4", Requisites(retired)), owner)).Result.ShouldBe(CaseWriteResult.InvalidTask);

        // Несогласованность с видом дела — последний рубеж за валидатором команды.
        (await cases.CreateAsync(InvestigationTestKit.Draft("УД-1", 5, 2, 10) with { TaskRequisites = Requisites(unit) }, owner))
            .Result.ShouldBe(CaseWriteResult.InvalidTask);
        (await cases.CreateAsync(InvestigationTestKit.Draft("Т-5", 5, 2, 10) with { Kind = CaseKind.ObjectTask }, owner))
            .Result.ShouldBe(CaseWriteResult.InvalidTask);
        (await cases.CreateAsync(TaskDraft("Т-6", Requisites(unit) with { Purpose = "  " }), owner)).Result.ShouldBe(CaseWriteResult.InvalidTask);

        // Ни одно из отклонённых дел не записано.
        (await cases.ListAsync(new CaseFilter(), owner)).TotalCount.ShouldBe(0);
        (await cases.CreateAsync(TaskDraft("Т-7", Requisites(unit) with { InitiatorRankId = rank }), owner)).Result.ShouldBe(CaseWriteResult.Ok);
    }

    [Fact(DisplayName = "Выключенный ГУ не мешает править старое задание, но заново его не выбрать; смена вида на «материал» очищает реквизиты задания")]
    public async Task Deactivated_initiator_is_kept_and_kind_change_clears_requisites()
    {
        var (factory, cases, references) = await ArrangeAsync();
        var unit = await CreateItemAsync(references, ReferenceKind.InitiatorUnit, "ГУ-1");
        var other = await CreateItemAsync(references, ReferenceKind.InitiatorUnit, "ГУ-2");
        var owner = InvestigationTestKit.Access(10, 9, 5);

        var created = await cases.CreateAsync(TaskDraft("Т-1", Requisites(unit)), owner);
        created.Result.ShouldBe(CaseWriteResult.Ok);

        (await references.SetActiveAsync(unit, false)).ShouldBe(ReferenceWriteResult.Ok);
        (await references.SetActiveAsync(other, false)).ShouldBe(ReferenceWriteResult.Ok);

        // Тот же (выключенный) ГУ — правка обоснования проходит.
        (await cases.UpdateAsync(created.CaseId, "Задание", CaseKind.ObjectTask, new DateOnly(2026, 9, 1), 10, null,
            Requisites(unit) with { Justification = "Уточнённое обоснование" }, owner)).ShouldBe(CaseWriteResult.Ok);
        (await cases.GetAsync(created.CaseId, owner)).ShouldNotBeNull().TaskRequisites.ShouldNotBeNull()
            .Justification.ShouldBe("Уточнённое обоснование");

        // Другой выключенный ГУ — отказ, дело не изменилось.
        (await cases.UpdateAsync(created.CaseId, "Задание", CaseKind.ObjectTask, new DateOnly(2026, 9, 1), 10, null,
            Requisites(other), owner)).ShouldBe(CaseWriteResult.InvalidTask);
        (await cases.GetAsync(created.CaseId, owner)).ShouldNotBeNull().TaskRequisites.ShouldNotBeNull()
            .InitiatorUnitId.ShouldBe(unit);

        // Смена вида: реквизиты задания уходят целиком, ограничение таблицы не нарушено.
        (await cases.UpdateAsync(created.CaseId, "Материал", CaseKind.Material, new DateOnly(2026, 9, 1), 10, null,
            null, owner)).ShouldBe(CaseWriteResult.Ok);
        (await cases.GetAsync(created.CaseId, owner)).ShouldNotBeNull().TaskRequisites.ShouldBeNull();

        await using var db = factory.CreateDbContext();
        var entity = await db.Cases.AsNoTracking().SingleAsync(c => c.Id == created.CaseId);
        entity.TaskNumber.ShouldBeNull();
        entity.InitiatorUnitId.ShouldBeNull();
        entity.Justification.ShouldBeNull();
        entity.Purpose.ShouldBeNull();
    }

    [Fact(DisplayName = "Справочник: наименование уникально в виде без учёта регистра, «%» и «_» сравниваются буквально; порядок; запись, на которую ссылается дело, не удалить даже SQL; CHECK задания держит инвариант в обход хранилища")]
    public async Task Reference_store_rules_and_table_constraints()
    {
        var (factory, cases, references) = await ArrangeAsync();

        var major = await CreateItemAsync(references, ReferenceKind.Rank, "Майор", sortOrder: 20);
        await CreateItemAsync(references, ReferenceKind.Rank, "Капитан", sortOrder: 10);
        (await references.CreateAsync(ReferenceKind.Rank, "  майор ", null, 0)).Result.ShouldBe(ReferenceWriteResult.Duplicate);

        // То же имя в другом справочнике — не дубликат.
        (await references.CreateAsync(ReferenceKind.Position, "Майор", null, 0)).Result.ShouldBe(ReferenceWriteResult.Ok);

        // «%» и «_» — не подстановочные: «М_йор» не совпадает с «Майор».
        (await references.CreateAsync(ReferenceKind.Rank, "М_йор", null, 30)).Result.ShouldBe(ReferenceWriteResult.Ok);
        (await references.CreateAsync(ReferenceKind.Rank, "%", null, 30)).Result.ShouldBe(ReferenceWriteResult.Ok);

        // Переименование в занятое — отказ; в своё же имя с другим регистром — можно.
        var captain = (await references.ListAsync(ReferenceKind.Rank)).Single(i => i.Name == "Капитан").Id;
        (await references.UpdateAsync(captain, "МАЙОР", null, 10)).ShouldBe(ReferenceWriteResult.Duplicate);
        (await references.UpdateAsync(major, "майор", "MJ", 20)).ShouldBe(ReferenceWriteResult.Ok);
        (await references.UpdateAsync(999_999, "x", null, 0)).ShouldBe(ReferenceWriteResult.NotFound);
        (await references.SetActiveAsync(999_999, false)).ShouldBe(ReferenceWriteResult.NotFound);

        // Порядок: сначала по SortOrder, затем по наименованию.
        var ranks = await references.ListAsync(ReferenceKind.Rank);
        ranks.Select(r => r.Name).Take(2).ShouldBe(["Капитан", "майор"]);

        // Запись, на которую ссылается задание, не удаляется даже прямым SQL (FK RESTRICT).
        var unit = await CreateItemAsync(references, ReferenceKind.InitiatorUnit, "ГУ-1");
        var owner = InvestigationTestKit.Access(10, 9, 5);
        var created = await cases.CreateAsync(TaskDraft("Т-1", Requisites(unit)), owner);
        created.Result.ShouldBe(CaseWriteResult.Ok);

        await using var db = factory.CreateDbContext();
        var deleteReferenced = async () => await db.Database.ExecuteSqlAsync(
            $"DELETE FROM investigation.reference_item WHERE id = {unit}");
        await deleteReferenced.ShouldThrowAsync<DbException>();

        // CHECK: задание без реквизитов и уголовное дело с реквизитами не записать и в обход хранилища.
        var criminal = await cases.CreateAsync(InvestigationTestKit.Draft("УД-1", 5, 2, 10), owner);
        var taskWithoutRequisites = async () => await db.Database.ExecuteSqlAsync(
            $"UPDATE investigation.case_file SET kind = 4 WHERE id = {criminal.CaseId}");
        await taskWithoutRequisites.ShouldThrowAsync<DbException>();
        var criminalWithRequisites = async () => await db.Database.ExecuteSqlAsync(
            $"UPDATE investigation.case_file SET kind = 1 WHERE id = {created.CaseId}");
        await criminalWithRequisites.ShouldThrowAsync<DbException>();

        // Обязательный реквизит из одних пробелов или NULL у задания не проходит (btrim(NULL) — NULL, отсюда coalesce).
        var blankPurpose = async () => await db.Database.ExecuteSqlAsync(
            $"UPDATE investigation.case_file SET purpose = '   ' WHERE id = {created.CaseId}");
        await blankPurpose.ShouldThrowAsync<DbException>();
        var nullNumber = async () => await db.Database.ExecuteSqlAsync(
            $"UPDATE investigation.case_file SET task_number = NULL WHERE id = {created.CaseId}");
        await nullNumber.ShouldThrowAsync<DbException>();

        // Функциональный индекс (kind, lower(name)) держит регистр и в обход хранилища (гонка двух записей).
        var sameNameOtherCase = async () => await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO investigation.reference_item (kind, name, sort_order, is_active, created_at) VALUES (2, 'КАПИТАН', 0, true, now())");
        await sameNameOtherCase.ShouldThrowAsync<DbException>();
    }

    [Fact(DisplayName = "Анкета: роль и поля сохраняются; год берётся из даты рождения; правка заменяет анкету целиком; CHECK не даёт году разойтись с датой")]
    public async Task Person_questionnaire_round_trip_and_birth_year_invariant()
    {
        var (factory, cases, references) = await ArrangeAsync();
        var unit = await CreateItemAsync(references, ReferenceKind.InitiatorUnit, "ГУ-1");
        var core = new CoreContextFactory(_postgres.GetConnectionString());
        var persons = InvestigationTestKit.CreatePersonStore(factory, core);
        var owner = InvestigationTestKit.Access(10, 9, 5);

        var created = await cases.CreateAsync(TaskDraft("Т-1", Requisites(unit)), owner);

        // Год в черновике расходится с датой — хранилище берёт год из даты.
        var questionnaire = new PersonQuestionnaire(
            new DateOnly(1985, 3, 12), 1990, " г. Ош ", "ОсОО «Пример»", "г. Бишкек, ул. Токтогула, 1", PersonSex.Male, " Шторм ");
        var target = await persons.CreateAsync(
            new PersonDraft(created.CaseId, "Иванов Иван", false, null, null, PersonRole.Target, questionnaire), owner);
        target.Result.ShouldBe(PersonWriteResult.Ok);

        var row = (await persons.GetAsync(target.PersonId, owner)).ShouldNotBeNull();
        row.Role.ShouldBe(PersonRole.Target);
        var q = row.Questionnaire.ShouldNotBeNull();
        q.BirthDate.ShouldBe(new DateOnly(1985, 3, 12));
        q.BirthYear.ShouldBe(1985);
        q.BirthPlace.ShouldBe("г. Ош");
        q.Sex.ShouldBe(PersonSex.Male);
        q.Alias.ShouldBe("Шторм");

        // Правка: только год, роль «связь», остальное пусто — анкета заменяется целиком.
        (await persons.UpdateAsync(target.PersonId,
            new PersonDraft(0, "Иванов Иван", false, "брат", null, PersonRole.Link, new PersonQuestionnaire(BirthYear: 1986)), owner))
            .ShouldBe(PersonWriteResult.Ok);
        var updated = (await persons.GetAsync(target.PersonId, owner)).ShouldNotBeNull();
        updated.Role.ShouldBe(PersonRole.Link);
        updated.RoleInCase.ShouldBe("брат");
        updated.Questionnaire.ShouldBe(new PersonQuestionnaire(BirthYear: 1986));

        // Без анкеты — пустая анкета и роль по умолчанию «иная».
        var plain = await persons.CreateAsync(new PersonDraft(created.CaseId, "Петров", false, null, null), owner);
        var plainRow = (await persons.GetAsync(plain.PersonId, owner)).ShouldNotBeNull();
        plainRow.Role.ShouldBe(PersonRole.Other);
        plainRow.Questionnaire.ShouldBe(PersonQuestionnaire.Empty);

        await using var db = factory.CreateDbContext();
        var mismatch = async () => await db.Database.ExecuteSqlAsync(
            $"UPDATE investigation.person SET birth_date = DATE '1970-01-01', birth_year = 1971 WHERE id = {plain.PersonId}");
        await mismatch.ShouldThrowAsync<DbException>();
    }

    [Fact(DisplayName = "Миграция TaskRequisitesAndQuestionnaire: фигуранты, заведённые до неё, получают роль «иная», прежние дела проходят CHECK; откат при наличии заданий останавливается")]
    public async Task Migration_backfills_role_other_and_keeps_existing_cases_valid()
    {
        var factory = new InvestigationContextFactory(_postgres.GetConnectionString());

        await using (var db = factory.CreateDbContext())
        {
            await db.GetService<IMigrator>().MigrateAsync("20260918125632_CaseClosureAct");
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO investigation.case_file (number, title, kind, opened_at, division_id, classification, status, created_at) "
                + "VALUES ('УД-1', 'Кража', 1, DATE '2026-09-01', 5, 1, 1, now())");
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO investigation.person (case_id, display_name, is_unidentified, classification, division_id, created_at) "
                + "SELECT id, 'Иванов', false, 1, 5, now() FROM investigation.case_file");

            await db.Database.MigrateAsync();
        }

        await using (var db = factory.CreateDbContext())
        {
            (await db.Persons.AsNoTracking().SingleAsync()).Role.ShouldBe(PersonRole.Other);
            var legacy = await db.Cases.AsNoTracking().SingleAsync();
            legacy.Kind.ShouldBe(CaseKind.CriminalCase);
            legacy.TaskNumber.ShouldBeNull();
        }

        // Откат не должен молча стереть реквизиты заданий: при наличии дела вида 4 он останавливается целиком.
        var references = new ReferenceStore(factory);
        var (_, unit) = await references.CreateAsync(ReferenceKind.InitiatorUnit, "ГУ-1", null, 0);
        await using (var db = factory.CreateDbContext())
        {
            await db.Database.ExecuteSqlAsync(
                $"UPDATE investigation.case_file SET kind = 4, task_number = 'З-1', initiator_unit_id = {unit}, justification = 'о', purpose = 'ц'");

            var rollback = async () => await db.GetService<IMigrator>().MigrateAsync("20260918125632_CaseClosureAct");
            await rollback.ShouldThrowAsync<DbException>();
        }

        await using (var db = factory.CreateDbContext())
        {
            (await db.Cases.AsNoTracking().SingleAsync()).TaskNumber.ShouldBe("З-1");
        }
    }

    private async Task<(InvestigationContextFactory Factory, CaseStore Cases, ReferenceStore References)> ArrangeAsync()
    {
        var factory = new InvestigationContextFactory(_postgres.GetConnectionString());
        var core = new CoreContextFactory(_postgres.GetConnectionString());
        await InvestigationTestKit.MigrateAsync(factory);
        await InvestigationTestKit.AssignRolesAsync(factory, (10, InvestigationRole.Investigator));
        return (factory, InvestigationTestKit.CreateCaseStore(factory, core), new ReferenceStore(factory));
    }

    private static async Task<int> CreateItemAsync(ReferenceStore references, ReferenceKind kind, string name, int sortOrder = 0)
    {
        var (result, id) = await references.CreateAsync(kind, name, null, sortOrder);
        result.ShouldBe(ReferenceWriteResult.Ok);
        return id;
    }

    private static TaskRequisites Requisites(int initiatorUnitId) =>
        new("З-1", initiatorUnitId, "Обоснование", "Цель");

    private static CaseDraft TaskDraft(string number, TaskRequisites task) =>
        InvestigationTestKit.Draft(number, 5, 2, 10) with { Kind = CaseKind.ObjectTask, TaskRequisites = task };
}
