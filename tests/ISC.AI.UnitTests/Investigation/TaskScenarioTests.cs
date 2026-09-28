using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Investigation.Application.Features.Cases;
using ISC.AI.Profile.Investigation.Application.Features.Persons;
using ISC.AI.Profile.Investigation.Domain.Enums;
using ISC.AI.Profile.Investigation.Domain.Services;
using ISC.AI.Profile.Investigation.UI;
using NSubstitute;
using Shouldly;

namespace ISC.AI.UnitTests.Investigation;

/// <summary>
/// Сценарии задания по объекту и анкеты (ТФ-ДЕЛ-05, ТФ-ПЕР-01/05): реквизиты и анкета доходят до хранилища,
/// отказ хранилища по справочнику переводится в понятный ответ, в журнал не попадают режимные и персональные
/// сведения (ТБ-032); модели форм собирают команды так же, как их ждёт валидатор.
/// </summary>
public sealed class TaskScenarioTests
{
    private static readonly TaskRequisites Requisites =
        new("З-17/26", 3, "Проверка информации", "Установить связи", "Асанов А. А.", 4, 5, "+996 000", "каб. 1", "срочно");

    private readonly ICaseStore _cases = Substitute.For<ICaseStore>();
    private readonly IPersonStore _persons = Substitute.For<IPersonStore>();
    private readonly IUserRoleStore _roles = Substitute.For<IUserRoleStore>();
    private readonly ISubjectProvider _subject = Substitute.For<ISubjectProvider>();
    private readonly IAccessContextProvider _access = Substitute.For<IAccessContextProvider>();

    public TaskScenarioTests()
    {
        _subject.GetCurrentUserIdAsync(Arg.Any<CancellationToken>()).Returns((int?)42);
        _roles.GetRoleAsync(42, Arg.Any<CancellationToken>()).Returns(InvestigationRole.Investigator);
        _access.GetCurrentAsync(Arg.Any<CancellationToken>())
            .Returns(new AccessContext("42", MaxClassification: 2, AllowedDivisions: [5]));
        _cases.CreateAsync(Arg.Any<CaseDraft>(), Arg.Any<AccessContext>(), Arg.Any<CancellationToken>())
            .Returns((CaseWriteResult.Ok, 7));
        _persons.CreateAsync(Arg.Any<PersonDraft>(), Arg.Any<AccessContext>(), Arg.Any<CancellationToken>())
            .Returns((PersonWriteResult.Ok, 9));
    }

    private static CreateCaseCommand TaskCommand() => new(
        "Т-1", "Задание", CaseKind.ObjectTask, new DateOnly(2026, 9, 1), 42, DivisionId: 5, Classification: 2,
        TaskRequisites: Requisites);

    [Fact(DisplayName = "Заведение задания: реквизиты доходят до черновика хранилища без изменений")]
    public async Task Create_passes_requisites_to_draft()
    {
        var response = await new CreateCaseCommand.Handler(_cases, _roles, _subject, _access)
            .Handle(TaskCommand(), CancellationToken.None);

        response.Status.ShouldBeTrue();
        await _cases.Received(1).CreateAsync(
            Arg.Is<CaseDraft>(d => d.Kind == CaseKind.ObjectTask && d.TaskRequisites == Requisites),
            Arg.Any<AccessContext>(), Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "Отказ хранилища по реквизитам задания (справочник) → BadRequest с текстом про ТФ-ДЕЛ-05")]
    public async Task Invalid_task_is_bad_request()
    {
        _cases.CreateAsync(Arg.Any<CaseDraft>(), Arg.Any<AccessContext>(), Arg.Any<CancellationToken>())
            .Returns((CaseWriteResult.InvalidTask, 0));

        var response = await new CreateCaseCommand.Handler(_cases, _roles, _subject, _access)
            .Handle(TaskCommand(), CancellationToken.None);

        response.StatusCode.ShouldBe(ResponseStatusCode.BadRequest);
        response.StatusMessage.ShouldBe(CaseGuard.InvalidTask);
        CaseGuard.ToResponse(CaseWriteResult.InvalidTask).StatusCode.ShouldBe(ResponseStatusCode.BadRequest);
    }

    [Fact(DisplayName = "Аудит задания: в сводке ГУ-инициатор по идентификатору, но нет № задания, обоснования и данных инициатора (ТБ-032)")]
    public void Task_audit_summary_is_redacted()
    {
        var create = TaskCommand().AuditSummary.ShouldNotBeNull();
        var update = new UpdateCaseCommand(7, "Задание", CaseKind.ObjectTask, new DateOnly(2026, 9, 1), 42, null, Requisites)
            .AuditSummary.ShouldNotBeNull();

        foreach (var summary in new[] { create, update })
        {
            summary.ShouldContain("initiator=3");
            summary.ShouldNotContain("17/26");
            summary.ShouldNotContain("Асанов");
            summary.ShouldNotContain("Проверка");
            summary.ShouldNotContain("996");
        }
    }

    [Fact(DisplayName = "Новый фигурант: роль и анкета доходят до хранилища; в аудит — роль, но не псевдоним и не ФИО")]
    public async Task Create_person_passes_role_and_questionnaire()
    {
        var questionnaire = new PersonQuestionnaire(BirthYear: 1985, Alias: "Шторм");
        var command = new CreatePersonCommand(7, "Иванов", false, Role: PersonRole.Target, Questionnaire: questionnaire);

        var response = await new CreatePersonCommand.Handler(_persons, _roles, _subject, _access)
            .Handle(command, CancellationToken.None);

        response.Status.ShouldBeTrue();
        await _persons.Received(1).CreateAsync(
            Arg.Is<PersonDraft>(d => d.Role == PersonRole.Target && d.Questionnaire == questionnaire),
            Arg.Any<AccessContext>(), Arg.Any<CancellationToken>());
        command.AuditSummary.ShouldNotBeNull().ShouldContain("role=Target");
        command.AuditSummary.ShouldNotContain("Шторм");
        command.AuditSummary.ShouldNotContain("Иванов");
    }

    [Fact(DisplayName = "Правка фигуранта: роль и анкета заменяются целиком тем, что прислала форма")]
    public async Task Update_person_passes_role_and_questionnaire()
    {
        _persons.UpdateAsync(Arg.Any<int>(), Arg.Any<PersonDraft>(), Arg.Any<AccessContext>(), Arg.Any<CancellationToken>())
            .Returns(PersonWriteResult.Ok);
        var questionnaire = new PersonQuestionnaire(Residence: "г. Бишкек");

        var response = await new UpdatePersonCommand.Handler(_persons, _roles, _subject, _access)
            .Handle(new UpdatePersonCommand(9, " Иванов ", false, PersonRole.Link, questionnaire, 7, 4, " брат "), CancellationToken.None);

        response.Status.ShouldBeTrue();
        await _persons.Received(1).UpdateAsync(
            9,
            Arg.Is<PersonDraft>(d => d.DisplayName == "Иванов" && d.RoleInCase == "брат"
                && d.Role == PersonRole.Link && d.Questionnaire == questionnaire),
            Arg.Any<AccessContext>(), Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "Форма задания: полнота — по №, ГУ, обоснованию и цели; реквизиты собираются обрезанными, пустое — null")]
    public void Task_form_builds_requisites()
    {
        var form = new TaskRequisitesForm { TaskNumber = " З-1 ", InitiatorUnitId = 3, Justification = "Обоснование" };
        form.IsComplete.ShouldBeFalse();

        form.Purpose = " Цель ";
        form.InitiatorPhone = "   ";
        form.IsComplete.ShouldBeTrue();

        var requisites = form.ToRequisites();
        requisites.ShouldBe(new TaskRequisites("З-1", 3, "Обоснование", "Цель"));
        TaskRequisitesForm.From(Requisites).ToRequisites().ShouldBe(Requisites);
    }

    [Fact(DisplayName = "Форма анкеты: год рождения берётся из даты; пустые строки — null; туда и обратно без потерь")]
    public void Questionnaire_form_builds_questionnaire()
    {
        var form = new PersonQuestionnaireForm { BirthDate = new DateTime(1985, 3, 12), BirthYear = 1990, Alias = "  ", Sex = PersonSex.Male };

        var questionnaire = form.ToQuestionnaire();

        questionnaire.ShouldBe(new PersonQuestionnaire(new DateOnly(1985, 3, 12), 1985, Sex: PersonSex.Male));
        PersonQuestionnaireForm.From(questionnaire).ToQuestionnaire().ShouldBe(questionnaire);
        PersonQuestionnaireForm.From(null).ToQuestionnaire().ShouldBe(PersonQuestionnaire.Empty);
        QuestionnaireLabels.Birth(questionnaire).ShouldBe("12.03.1985");
        QuestionnaireLabels.Birth(new PersonQuestionnaire(BirthYear: 1985)).ShouldBe("1985 г.");
        QuestionnaireLabels.Birth(null).ShouldBe("—");
    }

    [Fact(DisplayName = "Варианты справочника: действующие записи вида и уже выбранная выключенная; имя неизвестной — «№ id»")]
    public void Reference_options_keep_selected_inactive_item()
    {
        ReferenceItemRow[] items =
        [
            new(1, ReferenceKind.InitiatorUnit, "ГУ-Б", null, 0, true),
            new(2, ReferenceKind.InitiatorUnit, "ГУ-А", null, 0, true),
            new(3, ReferenceKind.InitiatorUnit, "Старое ГУ", null, 0, false),
            new(4, ReferenceKind.Rank, "майор", null, 0, true),
        ];

        ReferenceLookup.Options(items, ReferenceKind.InitiatorUnit, selected: null).Select(i => i.Id).ShouldBe([2, 1]);
        ReferenceLookup.Options(items, ReferenceKind.InitiatorUnit, selected: 3).Select(i => i.Id).ShouldBe([2, 1, 3]);
        ReferenceLookup.Name(items, 3).ShouldBe("Старое ГУ");

        // Исходный выключенный ГУ старого задания остаётся в списке и после выбора другого — к нему можно вернуться.
        var form = TaskRequisitesForm.From(new TaskRequisites("З-1", 3, "о", "ц"));
        form.InitiatorUnitId = 1;
        ReferenceLookup.Options(items, ReferenceKind.InitiatorUnit, form.InitiatorUnitId, form.InitialUnitId)
            .Select(i => i.Id).ShouldBe([2, 1, 3]);
        ReferenceLookup.Name(items, 99).ShouldBe("№ 99");
        ReferenceLookup.Name(items, null).ShouldBe("—");
    }
}
