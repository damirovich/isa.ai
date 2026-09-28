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
/// Пересечения между делами (ТФ-ПЕР-07, ADR-0029) без БД: недоступный фигурант — «не найден»; решение принимают
/// только роли, ведущие дела (ТП-004); отказ хранилища — неразличимый ответ; в журнал не попадает значение
/// реквизита (ТБ-032).
/// </summary>
public sealed class IntersectionScenarioTests
{
    private readonly IIntersectionStore _store = Substitute.For<IIntersectionStore>();
    private readonly IUserRoleStore _roles = Substitute.For<IUserRoleStore>();
    private readonly ISubjectProvider _subject = Substitute.For<ISubjectProvider>();
    private readonly IAccessContextProvider _access = Substitute.For<IAccessContextProvider>();

    public IntersectionScenarioTests()
    {
        _subject.GetCurrentUserIdAsync(Arg.Any<CancellationToken>()).Returns((int?)42);
        _roles.GetRoleAsync(42, Arg.Any<CancellationToken>()).Returns(InvestigationRole.Investigator);
        _access.GetCurrentAsync(Arg.Any<CancellationToken>())
            .Returns(new AccessContext("42", MaxClassification: 2, AllowedDivisions: [5]));
    }

    [Fact(DisplayName = "Пересечения недоступного фигуранта — NotFound с общим текстом (ТБ-021)")]
    public async Task Inaccessible_person_is_not_found()
    {
        _store.FindForPersonAsync(9, Arg.Any<AccessContext>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<IntersectionRow>?)null);

        var response = await new GetPersonIntersectionsQuery.Handler(_store, _access)
            .Handle(new GetPersonIntersectionsQuery(9), CancellationToken.None);

        response.StatusCode.ShouldBe(ResponseStatusCode.NotFound);
        response.StatusMessage.ShouldBe(PersonGuard.NotFound);
    }

    [Fact(DisplayName = "Решение по пересечению: роль без права вести дела — отказ, хранилище не вызывается")]
    public async Task Review_requires_case_editor_role()
    {
        _roles.GetRoleAsync(42, Arg.Any<CancellationToken>()).Returns(InvestigationRole.Verifier);

        var response = await new ReviewIntersectionCommand.Handler(_store, _roles, _subject, _access).Handle(
            new ReviewIntersectionCommand(9, IntersectionKind.Vehicle, "01KG123ABC", 3, IntersectionDecision.Confirmed),
            CancellationToken.None);

        response.StatusCode.ShouldBe(ResponseStatusCode.BadRequest);
        response.StatusMessage.ShouldBe(RoleGuard.CaseDenied);
        await _store.DidNotReceiveWithAnyArgs().ReviewAsync(default, default, default!, default, default, default!, default);
    }

    [Fact(DisplayName = "Решение по невидимому пересечению — NotFound; сводка аудита без значения реквизита (ТБ-032)")]
    public async Task Review_not_found_and_audit_is_redacted()
    {
        _store.ReviewAsync(9, IntersectionKind.Vehicle, "01KG123ABC", 3, IntersectionDecision.Rejected,
                Arg.Any<AccessContext>(), Arg.Any<CancellationToken>())
            .Returns(PersonWriteResult.NotFound);
        var command = new ReviewIntersectionCommand(9, IntersectionKind.Vehicle, "01KG123ABC", 3, IntersectionDecision.Rejected);

        var response = await new ReviewIntersectionCommand.Handler(_store, _roles, _subject, _access)
            .Handle(command, CancellationToken.None);

        response.StatusCode.ShouldBe(ResponseStatusCode.NotFound);
        response.StatusMessage.ShouldBe(ReviewIntersectionCommand.NotFound);
        command.AuditSummary.ShouldBe("investigation:person:9:intersection:Vehicle:case:3:Rejected");
        command.AuditSummary.ShouldNotContain("01KG123ABC");
    }

    [Fact(DisplayName = "Валидатор решения: неизвестные вид и решение, пустой ключ и неположительные идентификаторы отклоняются")]
    public void Review_validator_rejects_bad_input()
    {
        var validator = new ReviewIntersectionValidator();

        validator.Validate(new ReviewIntersectionCommand(9, IntersectionKind.Address, "бишкек ул токтогула 1", 3, IntersectionDecision.Confirmed))
            .IsValid.ShouldBeTrue();
        validator.Validate(new ReviewIntersectionCommand(9, (IntersectionKind)99, "k", 3, IntersectionDecision.Confirmed)).IsValid.ShouldBeFalse();
        validator.Validate(new ReviewIntersectionCommand(9, IntersectionKind.Vehicle, "k", 3, (IntersectionDecision)0)).IsValid.ShouldBeFalse();
        validator.Validate(new ReviewIntersectionCommand(9, IntersectionKind.Vehicle, " ", 3, IntersectionDecision.Confirmed)).IsValid.ShouldBeFalse();
        validator.Validate(new ReviewIntersectionCommand(0, IntersectionKind.Vehicle, "k", 3, IntersectionDecision.Confirmed)).IsValid.ShouldBeFalse();
        validator.Validate(new ReviewIntersectionCommand(9, IntersectionKind.Vehicle, "k", 0, IntersectionDecision.Confirmed)).IsValid.ShouldBeFalse();
        validator.Validate(new ReviewIntersectionCommand(9, IntersectionKind.Vehicle, new string('x', 1001), 3, IntersectionDecision.Confirmed))
            .IsValid.ShouldBeFalse();
    }
}
