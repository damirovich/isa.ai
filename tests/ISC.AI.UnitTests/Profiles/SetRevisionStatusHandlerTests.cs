using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Inspector.Application.Features.Norms;
using ISC.AI.Profile.Inspector.Domain.Enums;
using ISC.AI.Profile.Inspector.Domain.Services;
using NSubstitute;
using Shouldly;

namespace ISC.AI.UnitTests.Profiles;

/// <summary>
/// Команда смены статуса редакции (Э4-02): делегирует доменной службе материализации. С постройкой
/// картотеки (2026-08-10) команда получила гард ведения и аудит — смена статуса меняет выдачу для
/// всех (GATE-3) и не должна быть доступна любому вошедшему.
/// </summary>
public sealed class SetRevisionStatusHandlerTests
{
    [Fact(DisplayName = "Смена статуса редакции: Администратор — зовёт материализатор, возвращает число чанков")]
    public async Task Administrator_delegates_to_materializer()
    {
        var materializer = Substitute.For<IRevisionStatusMaterializer>();
        materializer.SetStatusAsync(42, RevisionStatus.Repealed, Arg.Any<CancellationToken>()).Returns(3);

        var response = await new SetRevisionStatusCommand.Handler(materializer, Admin(), Caller(7))
            .Handle(new SetRevisionStatusCommand(42, RevisionStatus.Repealed), CancellationToken.None);

        response.Status.ShouldBeTrue();
        response.Data.ShouldBe(3);
        await materializer.Received(1).SetStatusAsync(42, RevisionStatus.Repealed, Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "Смена статуса редакции: не-Администратор при живом Администраторе — отказ, материализатор не тронут")]
    public async Task Non_administrator_is_denied()
    {
        var materializer = Substitute.For<IRevisionStatusMaterializer>();
        var roles = Substitute.For<IUserRoleStore>();
        roles.GetRoleAsync(7, Arg.Any<CancellationToken>()).Returns(UserRole.Inspector);
        roles.AnyAdministratorAsync(Arg.Any<CancellationToken>()).Returns(true);

        var response = await new SetRevisionStatusCommand.Handler(materializer, roles, Caller(7))
            .Handle(new SetRevisionStatusCommand(42, RevisionStatus.Repealed), CancellationToken.None);

        response.Status.ShouldBeFalse();
        response.StatusMessage.ShouldBe(NormGuard.Denied);
        await materializer.DidNotReceiveWithAnyArgs().SetStatusAsync(default, default, default);
    }

    private static IUserRoleStore Admin()
    {
        var roles = Substitute.For<IUserRoleStore>();
        roles.GetRoleAsync(7, Arg.Any<CancellationToken>()).Returns(UserRole.Administrator);
        return roles;
    }

    private static ISubjectProvider Caller(int userId)
    {
        var subjects = Substitute.For<ISubjectProvider>();
        subjects.GetCurrentUserIdAsync(Arg.Any<CancellationToken>()).Returns(userId);
        return subjects;
    }
}
