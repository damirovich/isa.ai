using System.Threading;
using System.Threading.Tasks;
using ISC.AI.Abstractions.Grounding;
using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Inspector.Application.Features.Methods;
using ISC.AI.Profile.Inspector.Domain.Enums;
using ISC.AI.Profile.Inspector.Domain.Services;
using NSubstitute;
using Shouldly;
using Xunit;

namespace ISC.AI.UnitTests.Profiles;

/// <summary>
/// Гарды реестра методик (§5.2.9): сохраняют Инспектор/Руководитель/Администратор, утверждают и
/// правят — только Руководитель/Администратор. Отказ — ДО обращения к хранилищу.
/// </summary>
public sealed class MethodRegistryHandlersTests
{
    [Fact(DisplayName = "Сохранение: Инспектор — черновик уходит в хранилище с субъектом и цитатами")]
    public async Task Inspector_saves_draft()
    {
        var store = Substitute.For<IMethodRegistryStore>();
        MethodDocumentDraft? captured = null;
        store.SaveAsync(Arg.Do<MethodDocumentDraft>(d => captured = d), Arg.Any<CancellationToken>()).Returns(11);

        var response = await new SaveMethodDocumentCommand.Handler(store, Roles(UserRole.Inspector), Caller(7))
            .Handle(new SaveMethodDocumentCommand(
                "Чек-лист", "Целевая", "ГИ", "текст",
                [new CitationCheck("Приказ N1", CitationStatus.Confirmed, 3)],
                AllCitationsConfirmed: true, Classification: 1), CancellationToken.None);

        response.Status.ShouldBeTrue();
        response.Data.ShouldBe(11);
        captured.ShouldNotBeNull();
        captured!.CreatedByUserId.ShouldBe(7);
        captured.Classification.ShouldBe<short>(1);
        captured.CitationsJson.ShouldNotBeNull();
        captured.CitationsJson.ShouldContain("N1");
    }

    [Fact(DisplayName = "Сохранение: Исполнитель при живом Администраторе — отказ, хранилище не тронуто")]
    public async Task Performer_cannot_save()
    {
        var store = Substitute.For<IMethodRegistryStore>();

        var response = await new SaveMethodDocumentCommand.Handler(store, Roles(UserRole.Performer), Caller(7))
            .Handle(new SaveMethodDocumentCommand("Чек-лист", "Целевая", "ГИ", "текст", null, true, 0),
                CancellationToken.None);

        response.Status.ShouldBeFalse();
        response.StatusMessage.ShouldBe(MethodRegistryGuard.SaveDenied);
        await store.DidNotReceiveWithAnyArgs().SaveAsync(default!, default);
    }

    [Fact(DisplayName = "Утверждение: Инспектор при живом Администраторе — отказ (только Руководитель/Администратор)")]
    public async Task Inspector_cannot_approve()
    {
        var store = Substitute.For<IMethodRegistryStore>();
        var access = Substitute.For<IAccessContextProvider>();

        var response = await new SetMethodDocumentStatusCommand.Handler(
                store, Roles(UserRole.Inspector), Caller(7), access)
            .Handle(new SetMethodDocumentStatusCommand(1, Approve: true), CancellationToken.None);

        response.Status.ShouldBeFalse();
        response.StatusMessage.ShouldBe(MethodRegistryGuard.ManageDenied);
        await store.DidNotReceiveWithAnyArgs().SetStatusAsync(default, default, default, default, default);
    }

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
