using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Investigation.Application.Features.Common;
using ISC.AI.Profile.Investigation.Application.Features.LinkEvents;
using ISC.AI.Profile.Investigation.Domain.Enums;
using ISC.AI.Profile.Investigation.Domain.Services;
using NSubstitute;
using Shouldly;
using Xunit;

namespace ISC.AI.UnitTests.Investigation;

/// <summary>
/// Связь объектов через событие — «дело-посредник» (ТФ-ПЕР-11, ADR-0056) без БД: самосвязь — отказ с текстом [АИС табл.
/// 7.1 № 3] и в валидаторе, и в обработчике; ровно одно событие (существующее или новое); госномер и адрес контакта —
/// с буквами или цифрами; исходы хранилища — понятные ответы; связь ведут роли, ведущие дела; в журнале — только
/// номера, без названия, адреса и ФИО (ТБ-032).
/// </summary>
public sealed class LinkEventScenarioTests
{
    private static readonly LinkParticipantDraft Contact = new(null, LinkLevel.Confirmed, true, "01 KG 123 ABC", "Toyota", "г. Ош, ул. Ленина, 1");
    private static readonly LinkEventDraft NewMeeting = new(LinkEventKind.Meeting, "Встреча у рынка", new DateOnly(2026, 10, 5), "обсуждали поставку");

    private readonly ILinkEventStore _store = Substitute.For<ILinkEventStore>();
    private readonly IUserRoleStore _roles = Substitute.For<IUserRoleStore>();
    private readonly ISubjectProvider _subject = Substitute.For<ISubjectProvider>();
    private readonly IAccessContextProvider _access = Substitute.For<IAccessContextProvider>();

    public LinkEventScenarioTests()
    {
        _subject.GetCurrentUserIdAsync(Arg.Any<CancellationToken>()).Returns((int?)42);
        _roles.GetRoleAsync(42, Arg.Any<CancellationToken>()).Returns(InvestigationRole.Investigator);
        _access.GetCurrentAsync(Arg.Any<CancellationToken>()).Returns(new AccessContext("42", MaxClassification: 2, AllowedDivisions: [5]));
    }

    [Fact(DisplayName = "Самосвязь: валидатор и обработчик отказывают текстом «Ошибка топологии…» [АИС табл. 7.1 № 3]")]
    public async Task Self_link_is_rejected()
    {
        var command = new LinkPersonsViaEventCommand(9, 9, null, NewMeeting, null, null, Contact);

        var errors = new LinkPersonsViaEventValidator().Validate(command).Errors;
        errors.ShouldContain(e => e.ErrorMessage == LinkEventGuard.SelfLink);
        LinkEventGuard.SelfLink.ShouldBe("Ошибка топологии: объект не может иметь ребро связи на самого себя.");

        _store.LinkAsync(Arg.Any<LinkPersonsDraft>(), Arg.Any<AccessContext>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns((LinkEventWriteResult.SelfLink, 0));
        var response = await Link(command);
        response.StatusCode.ShouldBe(ResponseStatusCode.BadRequest);
        response.StatusMessage.ShouldBe(LinkEventGuard.SelfLink);
    }

    [Fact(DisplayName = "Валидатор: ровно одно событие — существующее или новое; новое — с названием; вид и уровень — из перечней")]
    public void Event_choice_and_fields_are_checked()
    {
        var validator = new LinkPersonsViaEventValidator();
        validator.Validate(new LinkPersonsViaEventCommand(9, 10, null, NewMeeting, "водитель", null, Contact)).IsValid.ShouldBeTrue();
        validator.Validate(new LinkPersonsViaEventCommand(9, 10, 5, null, null, null, Contact)).IsValid.ShouldBeTrue();

        validator.Validate(new LinkPersonsViaEventCommand(9, 10, null, null, null, null, Contact)).Errors
            .ShouldContain(e => e.ErrorMessage == LinkPersonsViaEventValidator.EventRequired);
        validator.Validate(new LinkPersonsViaEventCommand(9, 10, 5, NewMeeting, null, null, Contact)).IsValid.ShouldBeFalse();
        validator.Validate(new LinkPersonsViaEventCommand(9, 10, null, NewMeeting with { Title = "  " }, null, null, Contact)).IsValid.ShouldBeFalse();
        validator.Validate(new LinkPersonsViaEventCommand(9, 10, null, NewMeeting with { Kind = (LinkEventKind)9 }, null, null, Contact))
            .IsValid.ShouldBeFalse();
        validator.Validate(new LinkPersonsViaEventCommand(9, 10, null, NewMeeting, null, null, Contact with { Level = (LinkLevel)7 }))
            .IsValid.ShouldBeFalse();
        validator.Validate(new LinkPersonsViaEventCommand(9, 10, null,
            NewMeeting with { Title = new string('т', LinkEventDraftValidator.MaxTitleLength + 1) }, null, null, Contact)).IsValid.ShouldBeFalse();
        validator.Validate(new LinkPersonsViaEventCommand(9, 10, null, NewMeeting,
            new string('р', LinkParticipantDraftValidator.MaxRoleLength + 1), null, Contact)).IsValid.ShouldBeFalse();
        validator.Validate(new LinkPersonsViaEventCommand(0, 10, null, NewMeeting, null, null, Contact)).IsValid.ShouldBeFalse();
    }

    [Fact(DisplayName = "Участие: госномер и адрес контакта необязательны, но если заданы — с буквами или цифрами (иначе по ним ничего не найти)")]
    public void Participation_requisites_must_normalize()
    {
        var validator = new LinkParticipantDraftValidator();
        validator.Validate(new LinkParticipantDraft(null, LinkLevel.Probable, false, null, null, null)).IsValid.ShouldBeTrue();
        validator.Validate(Contact).IsValid.ShouldBeTrue();
        validator.Validate(Contact with { VehiclePlate = " -- " }).IsValid.ShouldBeFalse();
        validator.Validate(Contact with { ContactAddress = " , . " }).IsValid.ShouldBeFalse();
        validator.Validate(Contact with { VehiclePlate = new string('1', LinkParticipantDraftValidator.MaxPlateLength + 1) }).IsValid.ShouldBeFalse();
        validator.Validate(Contact with { ContactAddress = new string('а', LinkParticipantDraftValidator.MaxAddressLength + 1) })
            .IsValid.ShouldBeFalse();
    }

    [Fact(DisplayName = "Связь: хранилище получает черновик как есть и номер того, кто заводит; ответ — номер события; в журнале — только номера")]
    public async Task Link_is_passed_to_store_and_audit_is_redacted()
    {
        _store.LinkAsync(Arg.Any<LinkPersonsDraft>(), Arg.Any<AccessContext>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns((LinkEventWriteResult.Ok, 77));
        var command = new LinkPersonsViaEventCommand(9, 10, null, NewMeeting, "водитель", "пассажир", Contact);

        (await Link(command)).Data.ShouldBe(77);

        await _store.Received(1).LinkAsync(
            new LinkPersonsDraft(9, 10, null, NewMeeting, "водитель", "пассажир", Contact), Arg.Any<AccessContext>(), 42, Arg.Any<CancellationToken>());
        command.AuditSummary.ShouldBe("investigation:event:new:link:person:9:person:10");
        (command with { EventId = 5, NewEvent = null }).AuditSummary.ShouldBe("investigation:event:5:link:person:9:person:10");
        new UpdateLinkEventCommand(5, NewMeeting).AuditSummary.ShouldBe("investigation:event:5:update");
        new UpdateEventParticipantCommand(3, Contact).AuditSummary.ShouldBe("investigation:event:participant:3:update;level=Confirmed;search=True");
        new RemoveEventParticipantCommand(3).AuditSummary.ShouldBe("investigation:event:participant:3:remove");
        new ListPersonEventsQuery(9).AuditSummary.ShouldBe("investigation:person:9:events:view");
        new SearchEventLinkCandidatesQuery("Иванов").AuditSummary.ShouldBe("investigation:event:person-search");
        foreach (var summary in new[] { command.AuditSummary, new UpdateLinkEventCommand(5, NewMeeting).AuditSummary })
        {
            summary.ShouldNotBeNull().ShouldNotContain("рынк");
            summary.ShouldNotContain("Ош");
        }
    }

    [Theory(DisplayName = "Исходы хранилища: уже связаны и «не найден» — понятные ответы; недоступное неотличимо от несуществующего")]
    [InlineData(LinkEventWriteResult.AlreadyLinked, ResponseStatusCode.BadRequest, LinkEventGuard.AlreadyLinked)]
    [InlineData(LinkEventWriteResult.NotFound, ResponseStatusCode.NotFound, LinkEventGuard.NotFound)]
    public async Task Store_outcomes_are_mapped(LinkEventWriteResult outcome, ResponseStatusCode status, string message)
    {
        _store.LinkAsync(Arg.Any<LinkPersonsDraft>(), Arg.Any<AccessContext>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns((outcome, 0));
        _store.RemoveParticipantAsync(3, Arg.Any<AccessContext>(), Arg.Any<CancellationToken>()).Returns(outcome);

        var link = await Link(new LinkPersonsViaEventCommand(9, 10, 5, null, null, null, Contact));
        var remove = await new RemoveEventParticipantCommand.Handler(_store, _roles, _subject, _access)
            .Handle(new RemoveEventParticipantCommand(3), CancellationToken.None);

        link.StatusCode.ShouldBe(status);
        link.StatusMessage.ShouldBe(message);
        remove.StatusCode.ShouldBe(status);
        remove.StatusMessage.ShouldBe(message);
    }

    [Theory(DisplayName = "Связи через события ведут только роли, ведущие дела; прочим — отказ, хранилище не трогается")]
    [InlineData(InvestigationRole.FaceExpert)]
    [InlineData(InvestigationRole.Verifier)]
    [InlineData(InvestigationRole.SecurityOfficer)]
    [InlineData(null)]
    public async Task Other_roles_cannot_link(InvestigationRole? role)
    {
        _roles.GetRoleAsync(42, Arg.Any<CancellationToken>()).Returns(role);
        _roles.AnyAdministratorAsync(Arg.Any<CancellationToken>()).Returns(true);

        var link = await Link(new LinkPersonsViaEventCommand(9, 10, null, NewMeeting, null, null, Contact));
        var update = await new UpdateEventParticipantCommand.Handler(_store, _roles, _subject, _access)
            .Handle(new UpdateEventParticipantCommand(3, Contact), CancellationToken.None);
        var edit = await new UpdateLinkEventCommand.Handler(_store, _roles, _subject, _access)
            .Handle(new UpdateLinkEventCommand(5, NewMeeting), CancellationToken.None);

        link.StatusMessage.ShouldBe(RoleGuard.CaseDenied);
        update.StatusMessage.ShouldBe(RoleGuard.CaseDenied);
        edit.StatusMessage.ShouldBe(RoleGuard.CaseDenied);
        await _store.DidNotReceiveWithAnyArgs().LinkAsync(default!, default!, default, default);
        await _store.DidNotReceiveWithAnyArgs().UpdateParticipantAsync(default, default!, default!, default);
        await _store.DidNotReceiveWithAnyArgs().UpdateEventAsync(default, default!, default!, default);
    }

    [Fact(DisplayName = "События фигуранта: недоступный фигурант — «не найден»; доступный — список с числом")]
    public async Task Person_events_list()
    {
        var access = await _access.GetCurrentAsync(CancellationToken.None);
        _store.ListForPersonAsync(9, access, Arg.Any<CancellationToken>()).Returns(
            [new LinkEventRow(5, LinkEventKind.Trip, "Поездка в Ош", null, null, 1, 5, 42, DateTime.UtcNow, [])]);
        _store.ListForPersonAsync(11, access, Arg.Any<CancellationToken>()).Returns((IReadOnlyList<LinkEventRow>?)null);
        var handler = new ListPersonEventsQuery.Handler(_store, _access);

        var found = await handler.Handle(new ListPersonEventsQuery(9), CancellationToken.None);
        var missing = await handler.Handle(new ListPersonEventsQuery(11), CancellationToken.None);

        found.Data.ShouldNotBeNull().ShouldHaveSingleItem().Title.ShouldBe("Поездка в Ош");
        missing.StatusCode.ShouldBe(ResponseStatusCode.NotFound);
    }

    [Fact(DisplayName = "Подписи: вид события и уровень связи — по-русски; уровень строчными, как в [АИС ПМИ 3]")]
    public void Labels_are_russian()
    {
        LinkEventKind.Trip.Label().ShouldBe("Совместная поездка");
        LinkLevel.Confirmed.Label().ShouldBe("верно");
        LinkLevel.Probable.Label().ShouldBe("предположительно");
        LinkLevel.Wrong.Label().ShouldBe("неверно");
    }

    private async Task<ResponseDto<int>> Link(LinkPersonsViaEventCommand command) =>
        await new LinkPersonsViaEventCommand.Handler(_store, _roles, _subject, _access).Handle(command, CancellationToken.None);
}
