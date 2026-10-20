using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Investigation.Application.Features.Common;
using ISC.AI.Profile.Investigation.Application.Features.Persons;
using ISC.AI.Profile.Investigation.Domain.Enums;
using ISC.AI.Profile.Investigation.Domain.Services;
using NSubstitute;
using Shouldly;

namespace ISC.AI.UnitTests.Investigation;

/// <summary>
/// Связи объекта, адреса и автотранспорт (ТФ-ПЕР-06) без БД: поля связи — только у роли «связь»; адрес и
/// транспорт ведут роли, ведущие дела (ТП-004); отказ хранилища по связи переводится в понятный ответ; в
/// журнал не попадают адрес и госномер (ТБ-032).
/// </summary>
public sealed class ObjectLinkScenarioTests
{
    private readonly IPersonStore _persons = Substitute.For<IPersonStore>();
    private readonly IPersonRequisiteStore _requisites = Substitute.For<IPersonRequisiteStore>();
    private readonly IUserRoleStore _roles = Substitute.For<IUserRoleStore>();
    private readonly ISubjectProvider _subject = Substitute.For<ISubjectProvider>();
    private readonly IAccessContextProvider _access = Substitute.For<IAccessContextProvider>();

    public ObjectLinkScenarioTests()
    {
        _subject.GetCurrentUserIdAsync(Arg.Any<CancellationToken>()).Returns((int?)42);
        _roles.GetRoleAsync(42, Arg.Any<CancellationToken>()).Returns(InvestigationRole.Investigator);
        _access.GetCurrentAsync(Arg.Any<CancellationToken>())
            .Returns(new AccessContext("42", MaxClassification: 2, AllowedDivisions: [5]));
        _requisites.SaveAddressAsync(Arg.Any<int>(), Arg.Any<int?>(), Arg.Any<PersonAddressDraft>(), Arg.Any<AccessContext>(), Arg.Any<CancellationToken>())
            .Returns((PersonWriteResult.Ok, 3));
        _requisites.SaveVehicleAsync(Arg.Any<int>(), Arg.Any<int?>(), Arg.Any<PersonVehicleDraft>(), Arg.Any<AccessContext>(), Arg.Any<CancellationToken>())
            .Returns((PersonWriteResult.Ok, 4));
    }

    [Fact(DisplayName = "Валидатор: поля связи у роли, отличной от «связь», отклоняются; у «связи» — проходят")]
    public void Link_fields_only_for_link_role()
    {
        var create = new CreatePersonValidator();
        create.Validate(new CreatePersonCommand(3, "Петров", false, Role: PersonRole.Link, LinkedToPersonId: 7, LinkTypeId: 4))
            .IsValid.ShouldBeTrue();
        create.Validate(new CreatePersonCommand(3, "Петров", false, Role: PersonRole.Other, LinkedToPersonId: 7))
            .IsValid.ShouldBeFalse();
        create.Validate(new CreatePersonCommand(3, "Петров", false, Role: PersonRole.Target, LinkTypeId: 4))
            .IsValid.ShouldBeFalse();

        var update = new UpdatePersonValidator();
        update.Validate(new UpdatePersonCommand(7, "Петров", false, PersonRole.Link, null, 8, 4)).IsValid.ShouldBeTrue();
        update.Validate(new UpdatePersonCommand(7, "Петров", false, PersonRole.Link, null, 7, 4)).IsValid.ShouldBeFalse();
        update.Validate(new UpdatePersonCommand(7, "Петров", false, PersonRole.Other, null, 8, null)).IsValid.ShouldBeFalse();
    }

    [Fact(DisplayName = "Отказ хранилища по связи (другое дело, неверный тип) → BadRequest с текстом про ТФ-ПЕР-06")]
    public async Task Invalid_link_is_bad_request()
    {
        _persons.CreateAsync(Arg.Any<PersonDraft>(), Arg.Any<AccessContext>(), Arg.Any<CancellationToken>())
            .Returns((PersonWriteResult.InvalidLink, 0));

        var response = await new CreatePersonCommand.Handler(_persons, _roles, _subject, _access)
            .Handle(new CreatePersonCommand(3, "Петров", false, Role: PersonRole.Link, LinkedToPersonId: 99), CancellationToken.None);

        response.StatusCode.ShouldBe(ResponseStatusCode.BadRequest);
        response.StatusMessage.ShouldBe(PersonGuard.InvalidLink);
        PersonGuard.ToResponse(PersonWriteResult.InvalidLink).StatusCode.ShouldBe(ResponseStatusCode.BadRequest);
    }

    [Fact(DisplayName = "Адрес и транспорт: адрес и госномер уходят в хранилище обрезанными, но не в сводку аудита (ТБ-032)")]
    public async Task Requisites_are_saved_and_audit_is_redacted()
    {
        var address = new SavePersonAddressCommand(9, null, AddressKind.Residence, "  г. Бишкек, ул. Токтогула, 1 ", " с 2025 ");
        var vehicle = new SavePersonVehicleCommand(9, 5, " 01 KG 123 ABC ", " Toyota ", "Camry");

        (await new SavePersonAddressCommand.Handler(_requisites, _roles, _subject, _access).Handle(address, CancellationToken.None))
            .Data.ShouldBe(3);
        (await new SavePersonVehicleCommand.Handler(_requisites, _roles, _subject, _access).Handle(vehicle, CancellationToken.None))
            .Data.ShouldBe(4);

        await _requisites.Received(1).SaveAddressAsync(
            9, null, new PersonAddressDraft(AddressKind.Residence, "г. Бишкек, ул. Токтогула, 1", "с 2025"),
            Arg.Any<AccessContext>(), Arg.Any<CancellationToken>());
        await _requisites.Received(1).SaveVehicleAsync(
            9, 5, new PersonVehicleDraft("01 KG 123 ABC", "Toyota", "Camry", null, null),
            Arg.Any<AccessContext>(), Arg.Any<CancellationToken>());

        address.AuditSummary.ShouldBe("investigation:person:9:address:add:kind=Residence");
        vehicle.AuditSummary.ShouldBe("investigation:person:9:vehicle:update:5");
        vehicle.AuditSummary.ShouldNotContain("123");
    }

    [Theory(DisplayName = "Адрес и транспорт ведут только роли, ведущие дела; прочим — отказ, хранилище не трогается")]
    [InlineData(InvestigationRole.FaceExpert)]
    [InlineData(InvestigationRole.Verifier)]
    [InlineData(InvestigationRole.SecurityOfficer)]
    [InlineData(null)]
    public async Task Other_roles_cannot_edit_requisites(InvestigationRole? role)
    {
        _roles.GetRoleAsync(42, Arg.Any<CancellationToken>()).Returns(role);
        _roles.AnyAdministratorAsync(Arg.Any<CancellationToken>()).Returns(true);

        var save = await new SavePersonAddressCommand.Handler(_requisites, _roles, _subject, _access)
            .Handle(new SavePersonAddressCommand(9, null, AddressKind.Stay, "Ош"), CancellationToken.None);
        var delete = await new DeletePersonVehicleCommand.Handler(_requisites, _roles, _subject, _access)
            .Handle(new DeletePersonVehicleCommand(4), CancellationToken.None);

        save.StatusMessage.ShouldBe(RoleGuard.CaseDenied);
        delete.Status.ShouldBeFalse();
        await _requisites.DidNotReceiveWithAnyArgs().SaveAddressAsync(default, default, default!, default!, default);
        await _requisites.DidNotReceiveWithAnyArgs().DeleteVehicleAsync(default, default!, default);
    }

    [Fact(DisplayName = "Валидаторы: адрес без букв и цифр, транспорт без госномера и марки, госномер из одних знаков — отказ")]
    public void Requisite_validators_reject_empty_values()
    {
        var address = new SavePersonAddressValidator();
        address.Validate(new SavePersonAddressCommand(9, null, AddressKind.Residence, "г. Ош")).IsValid.ShouldBeTrue();
        address.Validate(new SavePersonAddressCommand(9, null, AddressKind.Residence, " -- ")).IsValid.ShouldBeFalse();
        address.Validate(new SavePersonAddressCommand(9, null, (AddressKind)9, "г. Ош")).IsValid.ShouldBeFalse();

        var vehicle = new SavePersonVehicleValidator();
        vehicle.Validate(new SavePersonVehicleCommand(9, null, "01 KG 123 ABC")).IsValid.ShouldBeTrue();
        vehicle.Validate(new SavePersonVehicleCommand(9, null, null, "Toyota")).IsValid.ShouldBeTrue();
        vehicle.Validate(new SavePersonVehicleCommand(9, null, " ", " ")).IsValid.ShouldBeFalse();
        vehicle.Validate(new SavePersonVehicleCommand(9, null, "---", "Toyota")).IsValid.ShouldBeFalse();
        vehicle.Validate(new SavePersonVehicleCommand(9, null, new string('1', SavePersonVehicleValidator.MaxPlateLength + 1)))
            .IsValid.ShouldBeFalse();
    }

    [Fact(DisplayName = "ADR-0055: телефон уходит в хранилище обрезанным и как введён, но не в сводку аудита (ТБ-032)")]
    public async Task Phone_is_saved_and_audit_is_redacted()
    {
        _requisites.SavePhoneAsync(Arg.Any<int>(), Arg.Any<int?>(), Arg.Any<PersonPhoneDraft>(), Arg.Any<AccessContext>(), Arg.Any<CancellationToken>())
            .Returns((PersonWriteResult.Ok, 6));
        var add = new SavePersonPhoneCommand(9, null, PhoneKind.Mobile, " +996 555 123 456 ", "  ");
        var update = new SavePersonPhoneCommand(9, 6, PhoneKind.Work, "0312 55-66-77", " приёмная ");

        (await new SavePersonPhoneCommand.Handler(_requisites, _roles, _subject, _access).Handle(add, CancellationToken.None))
            .Data.ShouldBe(6);
        (await new SavePersonPhoneCommand.Handler(_requisites, _roles, _subject, _access).Handle(update, CancellationToken.None))
            .Data.ShouldBe(6);

        await _requisites.Received(1).SavePhoneAsync(
            9, null, new PersonPhoneDraft(PhoneKind.Mobile, "+996 555 123 456", null), Arg.Any<AccessContext>(), Arg.Any<CancellationToken>());
        await _requisites.Received(1).SavePhoneAsync(
            9, 6, new PersonPhoneDraft(PhoneKind.Work, "0312 55-66-77", "приёмная"), Arg.Any<AccessContext>(), Arg.Any<CancellationToken>());

        add.AuditSummary.ShouldBe("investigation:person:9:phone:add");
        update.AuditSummary.ShouldBe("investigation:person:9:phone:update:6");
        new DeletePersonPhoneCommand(6).AuditSummary.ShouldBe("investigation:person:phone:6:delete");
        add.AuditSummary.ShouldNotBeNull().ShouldNotContain("555");
    }

    [Fact(DisplayName = "ADR-0055: номер без цифр — отказ и в обработчике (прямой вызов), чужой фигурант — «не найден»")]
    public async Task Phone_handler_rejects_no_digits_and_maps_not_found()
    {
        _requisites.SavePhoneAsync(Arg.Any<int>(), Arg.Any<int?>(), Arg.Any<PersonPhoneDraft>(), Arg.Any<AccessContext>(), Arg.Any<CancellationToken>())
            .Returns((PersonWriteResult.NotFound, 0));
        var handler = new SavePersonPhoneCommand.Handler(_requisites, _roles, _subject, _access);

        var noDigits = await handler.Handle(new SavePersonPhoneCommand(9, null, PhoneKind.Mobile, "нет"), CancellationToken.None);
        var foreign = await handler.Handle(new SavePersonPhoneCommand(9, null, PhoneKind.Mobile, "0555 123 456"), CancellationToken.None);

        noDigits.StatusCode.ShouldBe(ResponseStatusCode.BadRequest);
        noDigits.StatusMessage.ShouldBe(SavePersonPhoneValidator.DigitsRequiredMessage);
        foreign.StatusCode.ShouldBe(ResponseStatusCode.NotFound);
        await _requisites.Received(1).SavePhoneAsync(9, null, Arg.Any<PersonPhoneDraft>(), Arg.Any<AccessContext>(), Arg.Any<CancellationToken>());
    }

    [Theory(DisplayName = "ADR-0055: телефоны ведут только роли, ведущие дела; прочим — отказ, хранилище не трогается")]
    [InlineData(InvestigationRole.FaceExpert)]
    [InlineData(InvestigationRole.Verifier)]
    [InlineData(InvestigationRole.SecurityOfficer)]
    [InlineData(null)]
    public async Task Other_roles_cannot_edit_phones(InvestigationRole? role)
    {
        _roles.GetRoleAsync(42, Arg.Any<CancellationToken>()).Returns(role);
        _roles.AnyAdministratorAsync(Arg.Any<CancellationToken>()).Returns(true);

        var save = await new SavePersonPhoneCommand.Handler(_requisites, _roles, _subject, _access)
            .Handle(new SavePersonPhoneCommand(9, null, PhoneKind.Mobile, "0555 123 456"), CancellationToken.None);
        var delete = await new DeletePersonPhoneCommand.Handler(_requisites, _roles, _subject, _access)
            .Handle(new DeletePersonPhoneCommand(6), CancellationToken.None);

        save.StatusMessage.ShouldBe(RoleGuard.CaseDenied);
        delete.StatusMessage.ShouldBe(RoleGuard.CaseDenied);
        await _requisites.DidNotReceiveWithAnyArgs().SavePhoneAsync(default, default, default!, default!, default);
        await _requisites.DidNotReceiveWithAnyArgs().DeletePhoneAsync(default, default!, default);
    }

    [Fact(DisplayName = "ADR-0055: валидатор телефона — нужен номер с цифрами, известный вид, пределы длины номера, цифр и примечания")]
    public void Phone_validator_rules()
    {
        var validator = new SavePersonPhoneValidator();
        validator.Validate(new SavePersonPhoneCommand(9, null, PhoneKind.Mobile, "+996 (555) 12-34-56")).IsValid.ShouldBeTrue();
        validator.Validate(new SavePersonPhoneCommand(9, 3, PhoneKind.Other, "112")).IsValid.ShouldBeTrue();

        var noDigits = validator.Validate(new SavePersonPhoneCommand(9, null, PhoneKind.Mobile, "+ ( ) -"));
        noDigits.IsValid.ShouldBeFalse();
        noDigits.Errors.ShouldContain(e => e.ErrorMessage == SavePersonPhoneValidator.DigitsRequiredMessage);
        validator.Validate(new SavePersonPhoneCommand(9, null, PhoneKind.Mobile, " ")).IsValid.ShouldBeFalse();
        validator.Validate(new SavePersonPhoneCommand(9, null, (PhoneKind)9, "0555 123 456")).IsValid.ShouldBeFalse();
        validator.Validate(new SavePersonPhoneCommand(0, null, PhoneKind.Mobile, "0555 123 456")).IsValid.ShouldBeFalse();
        validator.Validate(new SavePersonPhoneCommand(9, 0, PhoneKind.Mobile, "0555 123 456")).IsValid.ShouldBeFalse();
        validator.Validate(new SavePersonPhoneCommand(9, null, PhoneKind.Mobile, new string('1', SavePersonPhoneValidator.MaxDigits + 1)))
            .IsValid.ShouldBeFalse();
        validator.Validate(new SavePersonPhoneCommand(9, null, PhoneKind.Mobile, "1 " + new string(' ', SavePersonPhoneValidator.MaxNumberLength)))
            .IsValid.ShouldBeFalse();
        validator.Validate(new SavePersonPhoneCommand(
            9, null, PhoneKind.Mobile, "112", new string('п', SavePersonPhoneValidator.MaxNotesLength + 1))).IsValid.ShouldBeFalse();
    }
}
