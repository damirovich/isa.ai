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
}
