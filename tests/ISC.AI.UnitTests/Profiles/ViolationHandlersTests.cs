using System;
using System.Threading;
using System.Threading.Tasks;
using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Inspector.Application.Features.ViolationCategories;
using ISC.AI.Profile.Inspector.Application.Features.Violations;
using ISC.AI.Profile.Inspector.Domain.Enums;
using ISC.AI.Profile.Inspector.Domain.Services;
using NSubstitute;
using Shouldly;
using Xunit;

namespace ISC.AI.UnitTests.Profiles;

/// <summary>
/// Гарды учёта нарушений (Э5-01): заносят Инспектор/Руководитель/Администратор, Исполнителю
/// запись не положена (он объект контроля, §2.1); классификатор ведёт только Администратор.
/// Хранилище — заглушка: здесь проверяется именно то, что отказ происходит ДО обращения к данным.
/// </summary>
public sealed class ViolationHandlersTests
{
    [Fact(DisplayName = "Занесение нарушения: Инспектор — хранилище вызвано, id возвращён")]
    public async Task Inspector_creates_violation()
    {
        var store = Substitute.For<IViolationStore>();
        store.CreateAsync(Arg.Any<ViolationDraft>(), Arg.Any<CancellationToken>())
            .Returns((ViolationWriteResult.Ok, 5));

        var response = await new CreateViolationCommand.Handler(store, Roles(UserRole.Inspector), Caller(7))
            .Handle(Command(), CancellationToken.None);

        response.Status.ShouldBeTrue();
        response.Data.ShouldBe(5);
    }

    [Fact(DisplayName = "Занесение нарушения: Исполнитель при живом Администраторе — отказ, хранилище не тронуто")]
    public async Task Executor_is_denied()
    {
        var store = Substitute.For<IViolationStore>();

        var response = await new CreateViolationCommand.Handler(store, Roles(UserRole.Performer), Caller(7))
            .Handle(Command(), CancellationToken.None);

        response.Status.ShouldBeFalse();
        response.StatusMessage.ShouldBe(ViolationGuard.Denied);
        await store.DidNotReceiveWithAnyArgs().CreateAsync(default!, default);
    }

    [Fact(DisplayName = "Занесение нарушения: без субъекта — отказ (fail-closed)")]
    public async Task Unknown_subject_is_denied()
    {
        var store = Substitute.For<IViolationStore>();
        var subjects = Substitute.For<ISubjectProvider>();
        subjects.GetCurrentUserIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);

        var response = await new CreateViolationCommand.Handler(store, Roles(UserRole.Administrator), subjects)
            .Handle(Command(), CancellationToken.None);

        response.Status.ShouldBeFalse();
        response.StatusMessage.ShouldBe(ViolationGuard.Denied);
        await store.DidNotReceiveWithAnyArgs().CreateAsync(default!, default);
    }

    [Fact(DisplayName = "Классификатор: Инспектор при живом Администраторе — отказ, хранилище не тронуто")]
    public async Task Classifier_requires_administrator()
    {
        var store = Substitute.For<IViolationCategoryStore>();

        var response = await new SaveViolationCategoryCommand.Handler(store, Roles(UserRole.Inspector), Caller(7))
            .Handle(new SaveViolationCategoryCommand(null, "Новая сфера", null), CancellationToken.None);

        response.Status.ShouldBeFalse();
        response.StatusMessage.ShouldBe(SaveViolationCategoryCommand.Handler.Denied);
        await store.DidNotReceiveWithAnyArgs().CreateAsync(default!, default, default);
    }

    [Fact(DisplayName = "Валидатор: срок устранения раньше даты выявления — отказ; позже или пустой — принят")]
    public void Deadline_must_not_precede_detection_date()
    {
        var validator = new CreateViolationValidator();

        validator.Validate(Command() with { RemediationDeadline = new DateOnly(2026, 7, 31) })
            .IsValid.ShouldBeFalse();
        validator.Validate(Command() with { RemediationDeadline = new DateOnly(2026, 8, 1) })
            .IsValid.ShouldBeTrue();
        validator.Validate(Command()).IsValid.ShouldBeTrue();
    }

    private static CreateViolationCommand Command() => new(
        DivisionId: 1, CategoryId: 2, Severity: ViolationSeverity.High,
        DetectedAt: new DateOnly(2026, 8, 1), RemediationStatus: RemediationStatus.UnderControl);

    private static IUserRoleStore Roles(UserRole role)
    {
        var roles = Substitute.For<IUserRoleStore>();
        roles.GetRoleAsync(7, Arg.Any<CancellationToken>()).Returns(role);
        roles.AnyAdministratorAsync(Arg.Any<CancellationToken>()).Returns(true);
        return roles;
    }

    private static ISubjectProvider Caller(int userId)
    {
        var subjects = Substitute.For<ISubjectProvider>();
        subjects.GetCurrentUserIdAsync(Arg.Any<CancellationToken>()).Returns(userId);
        return subjects;
    }
}
