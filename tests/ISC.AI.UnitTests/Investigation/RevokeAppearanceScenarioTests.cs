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
/// Отзыв ошибочного появления (ADR-0034): право матрицы доступа — до обращения к хранилищу; ответы хранилища понятны
/// пользователю; причина обязательна и осмысленна; в журнал идёт только идентификатор.
/// </summary>
public sealed class RevokeAppearanceScenarioTests
{
    private readonly IPersonStore _persons = Substitute.For<IPersonStore>();
    private readonly IUserRoleStore _roles = Substitute.For<IUserRoleStore>();
    private readonly ISubjectProvider _subject = Substitute.For<ISubjectProvider>();
    private readonly IAccessContextProvider _access = Substitute.For<IAccessContextProvider>();

    public RevokeAppearanceScenarioTests()
    {
        _subject.GetCurrentUserIdAsync(Arg.Any<CancellationToken>()).Returns((int?)42);
        _roles.AnyAdministratorAsync(Arg.Any<CancellationToken>()).Returns(true);
        _access.GetCurrentAsync(Arg.Any<CancellationToken>()).Returns(new AccessContext("42", 2, [5]));
    }

    [Fact(DisplayName = "Следователю отзыв закрыт по умолчанию — отказ без обращения к хранилищу")]
    public async Task Investigator_is_denied()
    {
        _roles.GetRoleAsync(42, Arg.Any<CancellationToken>()).Returns(InvestigationRole.Investigator);

        var response = await Handle(new RevokeAppearanceCommand(7, "на кадре другой человек"));

        response.StatusCode.ShouldBe(ResponseStatusCode.BadRequest);
        response.StatusMessage.ShouldBe(RoleGuard.Denied(InvestigationPermissions.VerificationRevoke));
        await _persons.DidNotReceiveWithAnyArgs().RevokeAppearanceAsync(default, default!, default!);
    }

    [Theory(DisplayName = "Ответы хранилища переводятся в понятный текст; своё подтверждение отозвать нельзя")]
    [InlineData(AppearanceRevokeResult.Ok, true, null)]
    [InlineData(AppearanceRevokeResult.OwnDecision, false, RevokeAppearanceCommand.OwnDecision)]
    [InlineData(AppearanceRevokeResult.AlreadyRevoked, false, "Появление уже отозвано.")]
    [InlineData(AppearanceRevokeResult.NotFound, false, "Появление не найдено или недоступно.")]
    public async Task Store_results_are_explained(AppearanceRevokeResult result, bool ok, string? message)
    {
        _roles.GetRoleAsync(42, Arg.Any<CancellationToken>()).Returns(InvestigationRole.Head);
        _persons.RevokeAppearanceAsync(7, "на кадре другой человек", Arg.Any<AccessContext>(), Arg.Any<CancellationToken>()).Returns(result);

        var response = await Handle(new RevokeAppearanceCommand(7, "  на кадре другой человек "));

        response.Status.ShouldBe(ok);
        if (message is not null)
        {
            response.StatusMessage.ShouldBe(message);
        }
    }

    [Fact(DisplayName = "Причина обязательна (не короче 10 знаков, не длиннее 1000); в журнал — только номер появления")]
    public void Reason_is_required_and_not_audited()
    {
        var validator = new RevokeAppearanceValidator();
        validator.Validate(new RevokeAppearanceCommand(7, "на кадре другой человек")).IsValid.ShouldBeTrue();
        validator.Validate(new RevokeAppearanceCommand(7, "ошибка")).IsValid.ShouldBeFalse();
        validator.Validate(new RevokeAppearanceCommand(7, "   ")).IsValid.ShouldBeFalse();
        validator.Validate(new RevokeAppearanceCommand(7, new string('x', RevokeAppearanceValidator.MaxReasonLength + 1))).IsValid.ShouldBeFalse();
        validator.Validate(new RevokeAppearanceCommand(0, "на кадре другой человек")).IsValid.ShouldBeFalse();

        var command = new RevokeAppearanceCommand(7, "Иванов на кадре");
        command.AuditSummary.ShouldBe("investigation:appearance:7:revoke");
        command.AuditSummary.ShouldNotContain("Иванов");
    }

    private ValueTask<ResponseDto<bool>> Handle(RevokeAppearanceCommand command) =>
        new RevokeAppearanceCommand.Handler(_persons, _roles, _subject, _access).Handle(command, CancellationToken.None);
}
