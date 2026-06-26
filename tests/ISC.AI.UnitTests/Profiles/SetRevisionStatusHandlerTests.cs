using ISC.AI.Profile.Inspector.Application.Revisions;
using ISC.AI.Profile.Inspector.Domain.Enums;
using ISC.AI.Profile.Inspector.Domain.Services;
using NSubstitute;
using Shouldly;

namespace ISC.AI.UnitTests.Profiles;

/// <summary>Команда смены статуса редакции (Э4-02): делегирует доменной службе материализации.</summary>
public sealed class SetRevisionStatusHandlerTests
{
    [Fact(DisplayName = "Смена статуса редакции: зовёт материализатор, возвращает число затронутых чанков")]
    public async Task Delegates_to_materializer()
    {
        var materializer = Substitute.For<IRevisionStatusMaterializer>();
        materializer.SetStatusAsync(42, RevisionStatus.Repealed, Arg.Any<CancellationToken>()).Returns(3);

        var response = await new SetRevisionStatusCommand.Handler(materializer)
            .Handle(new SetRevisionStatusCommand(42, RevisionStatus.Repealed), CancellationToken.None);

        response.Status.ShouldBeTrue();
        response.Data.ShouldBe(3);
        await materializer.Received(1).SetStatusAsync(42, RevisionStatus.Repealed, Arg.Any<CancellationToken>());
    }
}
