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
/// Сводки и справки по бланку (ТФ-ДДЛ-04/05, ТФ-АДМ-06, ADR-0031) на настоящем Postgres: документ виден ровно тогда,
/// когда видно дело; редакции только добавляются; правка от устаревшей редакции не затирает чужую; «архивный»
/// вычисляется по часам; после окна правит только тот, кому Администратор разрешил, и только до срока;
/// собственный запрос Администратор не решает; уничтожение дела уносит документы.
/// </summary>
public sealed class InvestigationCaseReportsTests : IAsyncLifetime
{
    private const int Author = 10;
    private const int Stranger = 11;
    private const int Admin = 30;
    private const int SecondAdmin = 31;

    private readonly PostgreSqlContainer _postgres = TestPostgres.Create();

    public Task InitializeAsync() => _postgres.StartAsync();

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    [Fact(DisplayName = "Сводка: создание, список по дате, поиск подстрокой с «ё/е»; чужому следователю и чужому подразделению — не найдено")]
    public async Task Reports_are_listed_and_searched_under_lattice()
    {
        var kit = await ArrangeAsync();
        var author = InvestigationTestKit.Access(Author, 3, 5);
        var caseId = (await kit.Cases.CreateAsync(InvestigationTestKit.Draft("А-1/26", 5, 2, Author), author)).CaseId;
        var target = (await kit.Persons.CreateAsync(new PersonDraft(caseId, "Семёнов Пётр", false, null, null, PersonRole.Target), author)).PersonId;

        var summary = new CaseReportContent(Events:
        [
            new SummaryEvent(" 09:15 ", "г. Бишкек, ул. Токтогула, 1", "Выехал на автомобиле", "Ёлкин", "01KG123ABC"),
            new SummaryEvent(null, "  ", null, null, null), // пустая строка отбрасывается
        ]);
        var (created, reportId) = await kit.Reports.CreateAsync(
            new CaseReportDraft(caseId, CaseReportKind.SummaryOn, new DateOnly(2026, 9, 28), target, summary), author);
        created.ShouldBe(CaseReportWriteResult.Ok);
        (await kit.Reports.CreateAsync(
            new CaseReportDraft(caseId, CaseReportKind.ReferenceUn, new DateOnly(2026, 9, 29), null, new CaseReportContent(Identity: "Родился в 1990 г."))
            , author)).Result.ShouldBe(CaseReportWriteResult.Ok);

        var list = (await kit.Reports.ListByCaseAsync(caseId, null, author)).ShouldNotBeNull();
        list.Select(r => r.ReportDate).ShouldBe([new DateOnly(2026, 9, 29), new DateOnly(2026, 9, 28)]); // новые даты первыми
        var row = list.Single(r => r.Id == reportId);
        row.PersonName.ShouldBe("Семёнов Пётр");
        row.State.ShouldBe(CaseReportState.Editable);
        row.CurrentRevision.ShouldBe(1);

        (await kit.Reports.ListByCaseAsync(caseId, "ЕЛКИН", author)).ShouldNotBeNull().ShouldHaveSingleItem().Id.ShouldBe(reportId);
        (await kit.Reports.ListByCaseAsync(caseId, "токтогула", author)).ShouldNotBeNull().Count.ShouldBe(1);
        (await kit.Reports.ListByCaseAsync(caseId, "нет такого", author)).ShouldNotBeNull().ShouldBeEmpty();

        var details = (await kit.Reports.GetAsync(reportId, author)).ShouldNotBeNull();
        details.Content.EventRows.ShouldHaveSingleItem().Time.ShouldBe("09:15");
        details.CanEditNow.ShouldBeTrue();

        // Чужой следователь без дела и субъект другого подразделения: дело и документы неотличимы от отсутствующих.
        (await kit.Reports.ListByCaseAsync(caseId, null, InvestigationTestKit.Access(Stranger, 3, 5))).ShouldBeNull();
        (await kit.Reports.GetAsync(reportId, InvestigationTestKit.Access(Stranger, 3, 5))).ShouldBeNull();
        (await kit.Reports.GetAsync(reportId, InvestigationTestKit.Access(Admin, 3, 6))).ShouldBeNull();
        (await kit.Reports.UpdateAsync(reportId, 1, summary, InvestigationTestKit.Access(Stranger, 3, 5))).ShouldBe(CaseReportWriteResult.NotFound);

        // Объект — только фигурант этого дела.
        var otherCase = (await kit.Cases.CreateAsync(InvestigationTestKit.Draft("Б-2/26", 5, 2, Author), author)).CaseId;
        var otherPerson = (await kit.Persons.CreateAsync(new PersonDraft(otherCase, "Иванов", false, null, null), author)).PersonId;
        (await kit.Reports.CreateAsync(new CaseReportDraft(caseId, CaseReportKind.SummaryOn, new DateOnly(2026, 9, 28), otherPerson, CaseReportContent.Empty), author))
            .Result.ShouldBe(CaseReportWriteResult.InvalidPerson);
    }

    [Fact(DisplayName = "Редакции: правка — новая редакция, прежняя сохраняется; без изменений — без редакции; от устаревшей — Stale")]
    public async Task Revisions_are_append_only_and_stale_edits_are_rejected()
    {
        var kit = await ArrangeAsync();
        var author = InvestigationTestKit.Access(Author, 3, 5);
        var reportId = await CreateSummaryAsync(kit, author);

        var second = new CaseReportContent(Events: [new SummaryEvent("10:00", "Ош", "Встреча", null, null)]);
        (await kit.Reports.UpdateAsync(reportId, 1, second, author)).ShouldBe(CaseReportWriteResult.Ok);
        (await kit.Reports.UpdateAsync(reportId, 2, second, author)).ShouldBe(CaseReportWriteResult.Ok); // то же — без новой редакции
        (await kit.Reports.UpdateAsync(reportId, 1, new CaseReportContent(Conclusion: "устаревшая"), author)).ShouldBe(CaseReportWriteResult.Stale);

        var details = (await kit.Reports.GetAsync(reportId, author)).ShouldNotBeNull();
        details.Report.CurrentRevision.ShouldBe(2);
        details.Revisions.Select(r => r.Number).ShouldBe([2, 1]);
        details.Content.EventRows.ShouldHaveSingleItem().Place.ShouldBe("Ош");
        (await kit.Reports.GetRevisionAsync(reportId, 1, author)).ShouldNotBeNull().EventRows.ShouldHaveSingleItem().Place.ShouldBe("Бишкек");
        (await kit.Reports.GetRevisionAsync(reportId, 1, InvestigationTestKit.Access(Stranger, 3, 5))).ShouldBeNull();

        // Неактивный документ хранится, но не правится; активный — снова правится.
        (await kit.Reports.SetActiveAsync(reportId, false, author)).ShouldBe(CaseReportWriteResult.Ok);
        (await kit.Reports.GetAsync(reportId, author)).ShouldNotBeNull().Report.State.ShouldBe(CaseReportState.Inactive);
        (await kit.Reports.UpdateAsync(reportId, 2, CaseReportContent.Empty, author)).ShouldBe(CaseReportWriteResult.Inactive);
        (await kit.Reports.SetActiveAsync(reportId, true, author)).ShouldBe(CaseReportWriteResult.Ok);
        (await kit.Reports.UpdateAsync(reportId, 2, new CaseReportContent(Conclusion: "итог"), author)).ShouldBe(CaseReportWriteResult.Ok);
    }

    [Fact(DisplayName = "Окно закрылось: правка — только по разрешению Администратора себе, до срока, с причиной в редакции; свой запрос Администратор не решает")]
    public async Task Archived_report_is_edited_only_with_active_permit()
    {
        var kit = await ArrangeAsync();
        var author = InvestigationTestKit.Access(Author, 3, 5);
        var reportId = await CreateSummaryAsync(kit, author);
        var admin = InvestigationTestKit.Access(Admin, 3, 5);
        var edit = new CaseReportContent(Conclusion: "исправлено после окна");

        // В окне запрос не нужен.
        (await kit.Reports.RequestPermitAsync(reportId, "опечатка", author)).ShouldBe(CaseReportWriteResult.PermitNotNeeded);

        kit.Clock.Advance(TimeSpan.FromHours(48));
        (await kit.Reports.GetAsync(reportId, author)).ShouldNotBeNull().Report.State.ShouldBe(CaseReportState.Archived);
        (await kit.Reports.UpdateAsync(reportId, 1, edit, author)).ShouldBe(CaseReportWriteResult.WindowClosed);

        (await kit.Reports.RequestPermitAsync(reportId, "опечатка в адресе", author)).ShouldBe(CaseReportWriteResult.Ok);
        (await kit.Reports.RequestPermitAsync(reportId, "ещё раз", author)).ShouldBe(CaseReportWriteResult.PermitNotNeeded); // уже ждёт

        var pending = (await kit.Reports.ListPermitsAsync(pendingOnly: true, admin)).ShouldHaveSingleItem();
        pending.ReportId.ShouldBe(reportId);
        pending.Reason.ShouldBe("опечатка в адресе");
        pending.CaseNumber.ShouldBe("А-1/26");
        (await kit.Reports.UpdateAsync(reportId, 1, edit, author)).ShouldBe(CaseReportWriteResult.WindowClosed); // ещё не решено

        (await kit.Reports.DecidePermitAsync(pending.Id, true, admin)).ShouldBe(CaseReportWriteResult.Ok);
        (await kit.Reports.DecidePermitAsync(pending.Id, false, admin)).ShouldBe(CaseReportWriteResult.AlreadyDecided);

        // Разрешение — только тому, кто просил.
        var colleague = InvestigationTestKit.Access(Admin, 3, 5);
        (await kit.Reports.UpdateAsync(reportId, 1, edit, colleague)).ShouldBe(CaseReportWriteResult.WindowClosed);

        (await kit.Reports.GetAsync(reportId, author)).ShouldNotBeNull().CanEditNow.ShouldBeTrue();
        (await kit.Reports.UpdateAsync(reportId, 1, edit, author)).ShouldBe(CaseReportWriteResult.Ok);
        (await kit.Reports.GetAsync(reportId, author)).ShouldNotBeNull().Revisions[0].EditReason.ShouldBe("опечатка в адресе");

        // Срок разрешения истёк — снова закрыто.
        kit.Clock.Advance(TimeSpan.FromHours(24));
        (await kit.Reports.UpdateAsync(reportId, 2, new CaseReportContent(Conclusion: "поздно"), author)).ShouldBe(CaseReportWriteResult.WindowClosed);

        // Собственный запрос Администратор не решает; решает другой Администратор.
        (await kit.Reports.RequestPermitAsync(reportId, "свой запрос", admin)).ShouldBe(CaseReportWriteResult.Ok);
        var own = (await kit.Reports.ListPermitsAsync(pendingOnly: true, admin)).ShouldHaveSingleItem();
        (await kit.Reports.DecidePermitAsync(own.Id, true, admin)).ShouldBe(CaseReportWriteResult.SelfDecision);
        (await kit.Reports.DecidePermitAsync(own.Id, false, InvestigationTestKit.Access(SecondAdmin, 3, 5))).ShouldBe(CaseReportWriteResult.Ok);

        // Запрос выше допуска Администратора не виден и не решается.
        (await kit.Reports.ListPermitsAsync(pendingOnly: false, InvestigationTestKit.Access(SecondAdmin, 1, 5))).ShouldBeEmpty();
    }

    [Fact(DisplayName = "Уничтожение дела уносит документы, редакции и запросы каскадом (ADR-0025)")]
    public async Task Case_deletion_cascades_to_reports()
    {
        var kit = await ArrangeAsync();
        var author = InvestigationTestKit.Access(Author, 3, 5);
        var reportId = await CreateSummaryAsync(kit, author);
        kit.Clock.Advance(TimeSpan.FromHours(49));
        (await kit.Reports.RequestPermitAsync(reportId, "причина", author)).ShouldBe(CaseReportWriteResult.Ok);

        await using var db = kit.Factory.CreateDbContext();
        await db.Cases.ExecuteDeleteAsync();
        (await db.CaseReports.CountAsync()).ShouldBe(0);
        (await db.CaseReportRevisions.CountAsync()).ShouldBe(0);
        (await db.CaseReportPermits.CountAsync()).ShouldBe(0);
    }

    private static async Task<int> CreateSummaryAsync(Kit kit, ISC.AI.Abstractions.Security.AccessContext author)
    {
        var caseId = (await kit.Cases.CreateAsync(InvestigationTestKit.Draft("А-1/26", 5, 2, Author), author)).CaseId;
        var (result, id) = await kit.Reports.CreateAsync(new CaseReportDraft(
            caseId, CaseReportKind.SummaryOn, new DateOnly(2026, 9, 28), null,
            new CaseReportContent(Events: [new SummaryEvent("09:00", "Бишкек", "Выход из дома", null, null)])), author);
        result.ShouldBe(CaseReportWriteResult.Ok);
        return id;
    }

    private async Task<Kit> ArrangeAsync()
    {
        var factory = new InvestigationContextFactory(_postgres.GetConnectionString());
        var core = new CoreContextFactory(_postgres.GetConnectionString());
        await InvestigationTestKit.MigrateAsync(factory);
        await InvestigationTestKit.AssignRolesAsync(factory,
            (Author, InvestigationRole.Investigator), (Stranger, InvestigationRole.Investigator),
            (Admin, InvestigationRole.Administrator), (SecondAdmin, InvestigationRole.Administrator));
        var policy = new InvestigationAccessPolicy(factory);
        var roles = new UserRoleStore(core, factory);
        var clock = new ShiftingClock();
        return new Kit(
            factory,
            clock,
            InvestigationTestKit.CreateCaseStore(factory, core),
            InvestigationTestKit.CreatePersonStore(factory, core),
            new CaseReportStore(factory, policy, roles, new CaseReportOptions(), clock));
    }

    /// <summary>Настоящее время со сдвигом вперёд: <c>created_at</c> ставит БД-контекст по системным часам.</summary>
    private sealed class ShiftingClock : TimeProvider
    {
        private TimeSpan _shift;

        public void Advance(TimeSpan by) => _shift += by;

        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow + _shift;
    }

    private sealed record Kit(
        InvestigationContextFactory Factory,
        ShiftingClock Clock,
        CaseStore Cases,
        PersonStore Persons,
        CaseReportStore Reports);
}
