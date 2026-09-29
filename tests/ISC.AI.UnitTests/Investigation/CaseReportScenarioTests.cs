using DocumentFormat.OpenXml.Packaging;
using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Investigation.Application.Features.Common;
using ISC.AI.Profile.Investigation.Application.Features.Reports;
using ISC.AI.Profile.Investigation.Data.Reports;
using ISC.AI.Profile.Investigation.Domain.Enums;
using ISC.AI.Profile.Investigation.Domain.Services;
using NSubstitute;
using Shouldly;

namespace ISC.AI.UnitTests.Investigation;

/// <summary>
/// Сводки и справки по бланку (ТФ-ДДЛ-04/05, ADR-0031) без БД: окно редактирования, приведение полей бланка и текст
/// для поиска, охрана ролей у команд и очереди запросов, сводки аудита без содержимого (ТБ-032), выгрузка .docx
/// с грифом в теле и свойствах файла (ТБ-033).
/// </summary>
public sealed class CaseReportScenarioTests
{
    private readonly ICaseReportStore _store = Substitute.For<ICaseReportStore>();
    private readonly IUserRoleStore _roles = Substitute.For<IUserRoleStore>();
    private readonly ISubjectProvider _subject = Substitute.For<ISubjectProvider>();
    private readonly IAccessContextProvider _access = Substitute.For<IAccessContextProvider>();

    public CaseReportScenarioTests()
    {
        _subject.GetCurrentUserIdAsync(Arg.Any<CancellationToken>()).Returns((int?)42);
        _roles.GetRoleAsync(42, Arg.Any<CancellationToken>()).Returns(InvestigationRole.Investigator);
        _access.GetCurrentAsync(Arg.Any<CancellationToken>())
            .Returns(new AccessContext("42", MaxClassification: 2, AllowedDivisions: [5]));
    }

    [Fact(DisplayName = "Окно: до 48 ч — «редактируется», ровно 48 ч и позже — «архивный», неактивный — всегда «неактивный»")]
    public void Window_state_is_computed_from_creation_time()
    {
        var options = new CaseReportOptions();
        var created = new DateTime(2026, 9, 28, 10, 0, 0, DateTimeKind.Utc);

        options.StateAt(created, true, created.AddHours(47).AddMinutes(59)).ShouldBe(CaseReportState.Editable);
        options.StateAt(created, true, created.AddHours(48)).ShouldBe(CaseReportState.Archived);
        options.StateAt(created, false, created.AddMinutes(1)).ShouldBe(CaseReportState.Inactive);
        new CaseReportOptions(EditWindowHours: 24).StateAt(created, true, created.AddHours(25)).ShouldBe(CaseReportState.Archived);
    }

    [Fact(DisplayName = "Бланк: пустые строки событий отбрасываются, поля чужого вида не хранятся; поиск — нижний регистр и «ё» → «е»")]
    public void Content_is_normalized_per_kind_and_searchable()
    {
        var content = new CaseReportContent(
            Events: [new SummaryEvent(" 09:15 ", "Бишкек", "Встреча с Ёлкиным", null, "01KG123ABC"), new SummaryEvent(" ", null, null, null, null)],
            Identity: "не относится к сводке",
            Conclusion: " Вывод ");

        var summary = content.Normalize(CaseReportKind.SummaryOn);
        summary.EventRows.ShouldHaveSingleItem().Time.ShouldBe("09:15");
        summary.Identity.ShouldBeNull();
        summary.Conclusion.ShouldBe("Вывод");
        summary.SearchText(CaseReportKind.SummaryOn).ShouldBe("09:15 бишкек встреча с елкиным 01kg123abc вывод");

        var reference = content.Normalize(CaseReportKind.ReferenceUn);
        reference.EventRows.ShouldBeEmpty();
        reference.Identity.ShouldBe("не относится к сводке");

        CaseReportContent.FromJson(summary.ToJson()).ToJson().ShouldBe(summary.ToJson());
        CaseReportContent.FromJson("{битый json").ShouldBe(CaseReportContent.Empty);
        RequisiteNormalizer.SearchText("  Өмүр   ЁЛКИН ").ShouldBe("өмүр елкин");
    }

    [Fact(DisplayName = "Создание и правка: роль без права вести дела — отказ, хранилище не вызывается")]
    public async Task Commands_require_case_editor_role()
    {
        _roles.GetRoleAsync(42, Arg.Any<CancellationToken>()).Returns(InvestigationRole.Verifier);

        var create = await new CreateCaseReportCommand.Handler(_store, _roles, _subject, _access).Handle(
            new CreateCaseReportCommand(3, CaseReportKind.SummaryOn, new DateOnly(2026, 9, 28), null, CaseReportContent.Empty), CancellationToken.None);
        var update = await new UpdateCaseReportCommand.Handler(_store, _roles, _subject, _access).Handle(
            new UpdateCaseReportCommand(9, 1, CaseReportContent.Empty), CancellationToken.None);

        create.StatusMessage.ShouldBe(RoleGuard.CaseDenied);
        update.StatusMessage.ShouldBe(RoleGuard.CaseDenied);
        await _store.DidNotReceiveWithAnyArgs().CreateAsync(default!, default!, default);
        await _store.DidNotReceiveWithAnyArgs().UpdateAsync(default, default, default!, default!, default);
    }

    [Fact(DisplayName = "Правка после окна — понятный отказ с отсылкой к запросу; устаревшая редакция — «обновите», чужая не затёрта")]
    public async Task Update_results_are_explained()
    {
        _store.UpdateAsync(9, 1, Arg.Any<CaseReportContent>(), Arg.Any<AccessContext>(), Arg.Any<CancellationToken>())
            .Returns(CaseReportWriteResult.WindowClosed, CaseReportWriteResult.Stale);
        var handler = new UpdateCaseReportCommand.Handler(_store, _roles, _subject, _access);
        var command = new UpdateCaseReportCommand(9, 1, new CaseReportContent(Conclusion: "секретный текст"));

        var closed = await handler.Handle(command, CancellationToken.None);
        closed.StatusCode.ShouldBe(ResponseStatusCode.BadRequest);
        closed.StatusMessage.ShouldContain("разрешение");

        (await handler.Handle(command, CancellationToken.None)).StatusMessage.ShouldContain("Обновите");

        // В журнал — только идентификаторы, не поля бланка (ТБ-032).
        command.AuditSummary.ShouldBe("investigation:report:9:update:from:1");
        new ListCaseReportsQuery(3, "Ёлкин").AuditSummary.ShouldNotContain("Ёлкин");
        new RequestCaseReportPermitCommand(9, "опечатка в адресе").AuditSummary.ShouldNotContain("опечатка");
    }

    [Fact(DisplayName = "Очередь запросов и решение — только Администратору; следователю — отказ без обращения к хранилищу")]
    public async Task Permit_queue_is_admin_only()
    {
        var list = await new ListCaseReportPermitsQuery.Handler(_store, _roles, _subject, _access)
            .Handle(new ListCaseReportPermitsQuery(true), CancellationToken.None);
        var decide = await new DecideCaseReportPermitCommand.Handler(_store, _roles, _subject, _access)
            .Handle(new DecideCaseReportPermitCommand(5, true), CancellationToken.None);

        list.StatusCode.ShouldBe(ResponseStatusCode.BadRequest);
        decide.StatusCode.ShouldBe(ResponseStatusCode.BadRequest);
        await _store.DidNotReceiveWithAnyArgs().ListPermitsAsync(default, default!, default);
        await _store.DidNotReceiveWithAnyArgs().DecidePermitAsync(default, default, default!, default);

        _roles.GetRoleAsync(42, Arg.Any<CancellationToken>()).Returns(InvestigationRole.Administrator);
        _store.DecidePermitAsync(5, true, Arg.Any<AccessContext>(), Arg.Any<CancellationToken>()).Returns(CaseReportWriteResult.SelfDecision);
        (await new DecideCaseReportPermitCommand.Handler(_store, _roles, _subject, _access)
            .Handle(new DecideCaseReportPermitCommand(5, true), CancellationToken.None)).StatusMessage.ShouldContain("правило двух лиц");
    }

    [Fact(DisplayName = "Валидаторы: дата и вид обязательны, причина запроса не пустая, пределы длины полей")]
    public void Validators_check_required_fields()
    {
        var create = new CreateCaseReportValidator();
        create.Validate(new CreateCaseReportCommand(3, CaseReportKind.ReferenceUn, new DateOnly(2026, 9, 28), null, CaseReportContent.Empty))
            .IsValid.ShouldBeTrue();
        create.Validate(new CreateCaseReportCommand(3, (CaseReportKind)9, new DateOnly(2026, 9, 28), null, CaseReportContent.Empty)).IsValid.ShouldBeFalse();
        create.Validate(new CreateCaseReportCommand(3, CaseReportKind.SummaryOn, default, null, CaseReportContent.Empty)).IsValid.ShouldBeFalse();
        create.Validate(new CreateCaseReportCommand(3, CaseReportKind.SummaryOn, new DateOnly(2026, 9, 28), null,
            new CaseReportContent(Conclusion: new string('x', CaseReportGuard.MaxFieldLength + 1)))).IsValid.ShouldBeFalse();

        var permit = new RequestCaseReportPermitValidator();
        permit.Validate(new RequestCaseReportPermitCommand(9, "опечатка")).IsValid.ShouldBeTrue();
        permit.Validate(new RequestCaseReportPermitCommand(9, "  ")).IsValid.ShouldBeFalse();
    }

    [Fact(DisplayName = "Выгрузка .docx: гриф в теле и в свойствах файла, таблица событий со строкой данных, исполнитель")]
    public void Docx_carries_classification_and_events()
    {
        var bytes = new CaseReportDocxRenderer().RenderDocx(new CaseReportExport(
            CaseReportKind.SummaryOn, "А-1/26", new DateOnly(2026, 9, 28), "Семёнов Пётр", 3,
            new CaseReportContent(Events: [new SummaryEvent("09:15", "Бишкек", "Выехал", "Ёлкин", "01KG123ABC")], Conclusion: "Итог"),
            CaseReportGuard.Marking(2), "Иванов И. И."));

        using var stream = new MemoryStream(bytes);
        using var document = WordprocessingDocument.Open(stream, false);
        var text = document.MainDocumentPart!.Document.Body!.InnerText;

        text.ShouldStartWith("СЕКРЕТНО");
        text.ShouldContain("Сводка ОН за 28.09.2026");
        text.ShouldContain("Дело № А-1/26");
        text.ShouldContain("Выехал");
        text.ShouldContain("01KG123ABC");
        text.ShouldContain("Исполнитель: Иванов И. И.");
        document.MainDocumentPart.Document.Body.Descendants<DocumentFormat.OpenXml.Wordprocessing.Table>().ShouldHaveSingleItem();
        document.PackageProperties.Category.ShouldBe("СЕКРЕТНО");
        CaseReportGuard.Marking(0).ShouldBe("НЕСЕКРЕТНО");
    }
}
