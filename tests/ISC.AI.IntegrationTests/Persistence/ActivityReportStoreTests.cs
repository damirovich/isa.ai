using System;
using System.Linq;
using System.Threading.Tasks;
using ISC.AI.Profile.Investigation.Data;
using ISC.AI.Profile.Investigation.Domain.Entities;
using ISC.AI.Profile.Investigation.Domain.Enums;
using ISC.AI.Profile.Investigation.Domain.Services;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Xunit;

namespace ISC.AI.IntegrationTests.Persistence;

/// <summary>
/// Данные журнала суточной активности и статистики карточек объектов (ТФ-ОТЧ-05, ОР-6.0.1, ADR-0061) на настоящем
/// Postgres: записи периода из привязок носителей и документов и из сводок; дела — тем же правилом, что реестр
/// (Следователь — только свои), сводка выше допуска — не видна; отбор по группе; предел с пометкой; подписи объектов —
/// псевдоним, а без него ФИО; карточки «Задание по объекту» по инициаторам и статусам, гриф — максимум учтённых.
/// </summary>
/// <remarks>Требуется Docker.</remarks>
public sealed class ActivityReportStoreTests(MediaSnapshotFixture fixture) : IClassFixture<MediaSnapshotFixture>
{
    private const int HeadId = 51;
    private const int InvestigatorId = 52;
    private const int OtherId = 99;

    private static readonly DateTime From = new(2021, 5, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime To = new(2021, 6, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Before = new(2021, 4, 20, 9, 0, 0, DateTimeKind.Utc);

    private static DateTime At(int day, int hour) => new(2021, 5, day, hour, 0, 0, DateTimeKind.Utc);

    [Fact(DisplayName = "Журнал и статистика: записи периода под правилом дел и решёткой сводок; Следователь — свои дела; группа; предел; подписи объектов; карточки по инициаторам")]
    public async Task Activity_and_object_cards()
    {
        var w = await SeedAsync();
        var factory = fixture.Investigation;
        var store = new ActivityReportStore(factory, new InvestigationAccessPolicy(factory), new UserRoleStore(fixture.Core, factory));
        var head = InvestigationTestKit.Access(HeadId, 2, w.On, w.Ou);

        // Руководитель: всё в периоде и в допуске; сводка с грифом 3 и носитель до периода — нет.
        var all = await store.ListActivityAsync(From, To, null, 100, head);
        all.Truncated.ShouldBeFalse();
        all.Entries.Select(e => (e.Material, e.CaseId)).ShouldBe(
        [
            (ActivityMaterial.Media, w.Task1),
            (ActivityMaterial.Report, w.Task1),
            (ActivityMaterial.Media, w.Task2),
            (ActivityMaterial.Report, w.Task2),
            (ActivityMaterial.Document, w.Criminal),
        ]);
        var task2Report = all.Entries.Single(e => e.Material == ActivityMaterial.Report && e.CaseId == w.Task2);
        task2Report.TaskNumber.ShouldBe("З-102");
        task2Report.ReportKind.ShouldBe(CaseReportKind.SummaryOn);
        task2Report.ReportClassification.ShouldBe((short)2);
        all.Entries.Single(e => e.Material == ActivityMaterial.Report && e.CaseId == w.Task1).ReportIsActive.ShouldBeFalse();
        all.Entries.Single(e => e.Material == ActivityMaterial.Document).DocumentId.ShouldBe(2001);
        all.Entries.First().UserId.ShouldBe(InvestigatorId);
        all.CaseObjects[w.Task1].ShouldBe("Сокол, Иванов Иван");   // псевдоним, а без него — ФИО
        all.CaseObjects[w.Criminal].ShouldBe("Лис");
        all.CaseObjects.ShouldNotContainKey(w.Task2);                // у второго задания объекта нет — только связь
        all.Persons[w.LinkPerson].ShouldBe("Ворон");

        // Следователь — только дела, где он ведущий или автор (ТБ-071).
        var mine = await store.ListActivityAsync(From, To, null, 100, InvestigationTestKit.Access(InvestigatorId, 2, w.On, w.Ou));
        mine.Entries.Select(e => e.CaseId).Distinct().ShouldBe([w.Task1]);

        // Группа ОУ — дела подразделения ОУ.
        var ou = await store.ListActivityAsync(From, To, [w.Ou], 100, head);
        ou.Entries.Select(e => e.CaseId).Distinct().ShouldBe([w.Task2]);

        // Предел: первые по времени и пометка.
        var limited = await store.ListActivityAsync(From, To, null, 2, head);
        limited.Truncated.ShouldBeTrue();
        limited.Entries.Count.ShouldBe(2);
        limited.Entries[0].OccurredAtUtc.ShouldBe(At(3, 9));

        // Карточки объектов: только «Задание по объекту» периода в допуске — по инициаторам и статусам.
        var cards = await store.CountObjectCardsAsync(From, To, null, head);
        cards.Rows.OrderBy(r => r.InitiatorUnitId).Select(r => (r.InitiatorUnitId, r.Total, r.InProgress, r.Suspended, r.Closed)).ShouldBe(
        [
            ((int?)w.Initiator1, 1, 1, 0, 0),
            ((int?)w.Initiator2, 1, 0, 0, 1),
        ]);
        cards.MaxClassification.ShouldBe((short)2);
        (await store.CountObjectCardsAsync(From, To, [w.On], head)).Rows.ShouldHaveSingleItem().InitiatorUnitId.ShouldBe(w.Initiator1);
    }

    private sealed record World(
        int On, int Ou, int Initiator1, int Initiator2, int Task1, int Task2, int Criminal, int LinkPerson);

    private async Task<World> SeedAsync()
    {
        await InvestigationTestKit.AssignRolesAsync(fixture.Investigation, (HeadId, InvestigationRole.Head), (InvestigatorId, InvestigationRole.Investigator));
        await using var db = fixture.Investigation.CreateDbContext();

        var on = new Division { Name = "Отдел наблюдения (журнал)", Direction = CaseDirection.Surveillance };
        var ou = new Division { Name = "Отдел установки (журнал)", Direction = CaseDirection.Establishment };
        db.Divisions.AddRange(on, ou);
        var gu1 = new ReferenceItem { Kind = ReferenceKind.InitiatorUnit, Name = "ГУ-1 (журнал)" };
        var gu2 = new ReferenceItem { Kind = ReferenceKind.InitiatorUnit, Name = "ГУ-2 (журнал)" };
        db.ReferenceItems.AddRange(gu1, gu2);
        await db.SaveChangesAsync();

        var task1 = Case("З-1", CaseKind.ObjectTask, on.Id, 1, CaseStatus.InProgress, InvestigatorId, "З-101", gu1.Id);
        var task2 = Case("З-2", CaseKind.ObjectTask, ou.Id, 2, CaseStatus.Closed, OtherId, "З-102", gu2.Id);
        var criminal = Case("У-7", CaseKind.CriminalCase, on.Id, 1, CaseStatus.InProgress, OtherId, null, null);
        var old = Case("З-0", CaseKind.ObjectTask, on.Id, 1, CaseStatus.Suspended, OtherId, "З-100", gu2.Id);
        var secret = Case("З-9", CaseKind.ObjectTask, on.Id, 3, CaseStatus.InProgress, OtherId, "З-109", gu1.Id);
        db.Cases.AddRange(task1, task2, criminal, old, secret);
        await db.SaveChangesAsync();

        // Время заведения дел — после вставки: аудируемые сущности получают текущее время при сохранении.
        await db.Cases.Where(c => c.Id == old.Id).ExecuteUpdateAsync(s => s.SetProperty(c => c.CreatedAt, Before));
        await db.Cases.Where(c => c.Id != old.Id).ExecuteUpdateAsync(s => s.SetProperty(c => c.CreatedAt, At(2, 8)));

        var sokol = NewPerson(task1, "Иванов Пётр", "Сокол", PersonRole.Target);
        var ivanov = NewPerson(task1, "Иванов Иван", null, PersonRole.Target);
        var link = NewPerson(task2, "Петров Пётр", "Ворон", PersonRole.Link);
        var lis = NewPerson(criminal, "Лисин Лев", "Лис", PersonRole.Target);
        db.Persons.AddRange(sokol, ivanov, link, lis);
        await db.SaveChangesAsync();

        db.CaseMediaLinks.AddRange(
            new CaseMediaLink { CaseId = task1.Id, MediaAssetId = 1001, LinkedByUserId = InvestigatorId, CreatedAt = At(3, 9) },
            new CaseMediaLink { CaseId = task2.Id, MediaAssetId = 1002, LinkedByUserId = OtherId, CreatedAt = At(5, 10) },
            new CaseMediaLink { CaseId = criminal.Id, MediaAssetId = 1003, LinkedByUserId = OtherId, CreatedAt = Before });
        db.CaseDocumentLinks.Add(new CaseDocumentLink { CaseId = criminal.Id, DocFlowDocumentId = 2001, LinkedByUserId = OtherId, CreatedAt = At(9, 15) });
        var summary = Report(task2, CaseReportKind.SummaryOn, link.Id, 2, isActive: true);
        var inactive = Report(task1, CaseReportKind.ReferenceUn, null, 1, isActive: false);
        var aboveClearance = Report(task1, CaseReportKind.ReferenceUn, null, 3, isActive: true);
        db.CaseReports.AddRange(summary, inactive, aboveClearance);
        await db.SaveChangesAsync();
        await db.CaseReports.Where(r => r.Id == inactive.Id).ExecuteUpdateAsync(s => s.SetProperty(r => r.CreatedAt, At(4, 12)));
        await db.CaseReports.Where(r => r.Id == summary.Id).ExecuteUpdateAsync(s => s.SetProperty(r => r.CreatedAt, At(6, 11)));
        await db.CaseReports.Where(r => r.Id == aboveClearance.Id).ExecuteUpdateAsync(s => s.SetProperty(r => r.CreatedAt, At(7, 11)));

        return new World(on.Id, ou.Id, gu1.Id, gu2.Id, task1.Id, task2.Id, criminal.Id, link.Id);
    }

    private static CaseFile Case(
        string number, CaseKind kind, int divisionId, short classification, CaseStatus status, int investigator, string? taskNumber, int? initiator) => new()
    {
        Number = number,
        Title = "Дело " + number,
        Kind = kind,
        OpenedAt = new DateOnly(2021, 5, 2),
        InvestigatorUserId = investigator,
        CreatedByUserId = investigator,
        DivisionId = divisionId,
        Classification = classification,
        Status = status,
        TaskNumber = taskNumber,
        InitiatorUnitId = initiator,

        // У «Задания по объекту» реквизиты задания обязательны (ck_case_file_task_requisites, ТФ-ДЕЛ-05).
        Justification = kind == CaseKind.ObjectTask ? "обоснование" : null,
        Purpose = kind == CaseKind.ObjectTask ? "цель" : null,
    };

    private static Person NewPerson(CaseFile file, string name, string? alias, PersonRole role) => new()
    {
        CaseId = file.Id,
        DisplayName = name,
        Alias = alias,
        Role = role,
        Classification = file.Classification,
        DivisionId = file.DivisionId,
    };

    private static CaseReport Report(CaseFile file, CaseReportKind kind, int? personId, short classification, bool isActive) => new()
    {
        CaseId = file.Id,
        PersonId = personId,
        Kind = kind,
        ReportDate = new DateOnly(2021, 5, 6),
        IsActive = isActive,
        CreatedByUserId = file.InvestigatorUserId,
        CurrentRevision = 1,
        Classification = classification,
        DivisionId = file.DivisionId,
    };
}
