using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Investigation.Application.Features.Cases;
using ISC.AI.Profile.Investigation.Application.Features.Common;
using ISC.AI.Profile.Investigation.Domain.Enums;
using ISC.AI.Profile.Investigation.Domain.Services;
using NSubstitute;
using Shouldly;

namespace ISC.AI.UnitTests.Investigation;

/// <summary>
/// Заведение дела (ТФ-ДЕЛ-01): роль проверяется в обработчике (ТП-004), отказ по допуску хранилища
/// переводится в понятный ответ (ТБ-024), занятый номер — в конфликт. Без роли хранилище не вызывается вовсе.
/// </summary>
public sealed class CaseScenarioGuardTests
{
    private readonly ICaseStore _cases = Substitute.For<ICaseStore>();
    private readonly IUserRoleStore _roles = Substitute.For<IUserRoleStore>();
    private readonly ISubjectProvider _subject = Substitute.For<ISubjectProvider>();
    private readonly IAccessContextProvider _access = Substitute.For<IAccessContextProvider>();

    public CaseScenarioGuardTests()
    {
        _subject.GetCurrentUserIdAsync(Arg.Any<CancellationToken>()).Returns((int?)42);
        _access.GetCurrentAsync(Arg.Any<CancellationToken>())
            .Returns(new AccessContext("42", MaxClassification: 2, AllowedDivisions: [5]));
        _cases.CreateAsync(Arg.Any<CaseDraft>(), Arg.Any<AccessContext>(), Arg.Any<CancellationToken>())
            .Returns((CaseWriteResult.Ok, 7));
    }

    [Theory(DisplayName = "Следователь, Руководитель и Администратор заводят дела")]
    [InlineData(InvestigationRole.Investigator)]
    [InlineData(InvestigationRole.Head)]
    [InlineData(InvestigationRole.Administrator)]
    public async Task Case_editors_may_create(InvestigationRole role)
    {
        _roles.GetRoleAsync(42, Arg.Any<CancellationToken>()).Returns(role);

        var response = await CreateAsync();

        response.Status.ShouldBeTrue();
        response.Data.ShouldBe(7);
    }

    [Theory(DisplayName = "Эксперт, Верификатор, Офицер ИБ и субъект без роли дело завести не могут — хранилище не вызывается")]
    [InlineData(InvestigationRole.FaceExpert)]
    [InlineData(InvestigationRole.Verifier)]
    [InlineData(InvestigationRole.SecurityOfficer)]
    [InlineData(null)]
    public async Task Other_roles_are_denied_and_store_is_untouched(InvestigationRole? role)
    {
        _roles.GetRoleAsync(42, Arg.Any<CancellationToken>()).Returns(role);
        _roles.AnyAdministratorAsync(Arg.Any<CancellationToken>()).Returns(false);

        var response = await CreateAsync();

        response.Status.ShouldBeFalse();
        response.StatusCode.ShouldBe(ResponseStatusCode.BadRequest);
        response.StatusMessage.ShouldBe(RoleGuard.CaseDenied);
        await _cases.DidNotReceive().CreateAsync(
            Arg.Any<CaseDraft>(), Arg.Any<AccessContext>(), Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "Без аутентификации — отказ (fail-closed), даже если Администратора нет")]
    public async Task Anonymous_is_denied()
    {
        _subject.GetCurrentUserIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);
        _roles.AnyAdministratorAsync(Arg.Any<CancellationToken>()).Returns(false);

        (await CreateAsync()).Status.ShouldBeFalse();
        await _cases.DidNotReceive().CreateAsync(
            Arg.Any<CaseDraft>(), Arg.Any<AccessContext>(), Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "Гриф/подразделение вне допуска → BadRequest с текстом про ТБ-024")]
    public async Task Outside_clearance_is_bad_request()
    {
        _roles.GetRoleAsync(42, Arg.Any<CancellationToken>()).Returns(InvestigationRole.Investigator);
        _cases.CreateAsync(Arg.Any<CaseDraft>(), Arg.Any<AccessContext>(), Arg.Any<CancellationToken>())
            .Returns((CaseWriteResult.OutsideClearance, 0));

        var response = await CreateAsync();

        response.Status.ShouldBeFalse();
        response.StatusCode.ShouldBe(ResponseStatusCode.BadRequest);
        response.StatusMessage.ShouldBe(CaseGuard.OutsideClearance);
        response.StatusMessage.ShouldContain("ТБ-024");
    }

    [Fact(DisplayName = "Занятый номер дела → Conflict")]
    public async Task Duplicate_number_is_conflict()
    {
        _roles.GetRoleAsync(42, Arg.Any<CancellationToken>()).Returns(InvestigationRole.Head);
        _cases.CreateAsync(Arg.Any<CaseDraft>(), Arg.Any<AccessContext>(), Arg.Any<CancellationToken>())
            .Returns((CaseWriteResult.DuplicateNumber, 0));

        var response = await CreateAsync();

        response.Status.ShouldBeFalse();
        response.StatusCode.ShouldBe(ResponseStatusCode.Conflict);
    }

    [Fact(DisplayName = "Черновик несёт гриф/подразделение формы и создателя из контекста допуска (ТБ-024)")]
    public async Task Draft_carries_classification_division_and_creator()
    {
        _roles.GetRoleAsync(42, Arg.Any<CancellationToken>()).Returns(InvestigationRole.Investigator);

        await CreateAsync();

        await _cases.Received(1).CreateAsync(
            Arg.Is<CaseDraft>(d => d.Classification == 2 && d.DivisionId == 5 && d.CreatedByUserId == 42
                && d.Number == "УД-1" && d.Kind == CaseKind.CriminalCase),
            Arg.Any<AccessContext>(), Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "Аудит: сводка без номера дела, гриф записи — гриф дела (ТБ-032)")]
    public void Audit_summary_omits_number_and_classifies_by_case()
    {
        var command = Command();

        command.AuditClassification.ShouldBe((short)2);
        command.AuditSummary.ShouldNotBeNull().ShouldNotContain("УД-1");
    }

    private static CreateCaseCommand Command() => new(
        "УД-1", "Кража", CaseKind.CriminalCase, new DateOnly(2026, 9, 1),
        InvestigatorUserId: 42, DivisionId: 5, Classification: 2, Basis: null);

    private async Task<ResponseDto<int>> CreateAsync()
    {
        var handler = new CreateCaseCommand.Handler(_cases, _roles, _subject, _access);
        return await handler.Handle(Command(), CancellationToken.None);
    }
}
