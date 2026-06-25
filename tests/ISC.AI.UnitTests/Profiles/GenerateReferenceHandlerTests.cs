using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Grounding;
using ISC.AI.Abstractions.Rag;
using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Inspector.Application.Generation;
using NSubstitute;
using Shouldly;

namespace ISC.AI.UnitTests.Profiles;

/// <summary>
/// Сценарий «Генератор» (Э4-03, ТФ-ГЕН-01): вложенный handler зовёт RAG-оркестратор ядра с задачным
/// промптом и контекстом доступа, маппит грунтовку и наследование грифа в конверт <see cref="ResponseDto{T}"/>,
/// всегда помечает результат как требующий проверки человеком (HITL, ТБ-042). Валидация входа — в
/// сквозном ValidationBehavior (см. <c>GenerateReferenceValidatorTests</c>).
/// </summary>
public sealed class GenerateReferenceHandlerTests
{
    [Fact(DisplayName = "Генератор: оркестратор зовётся с задачным промптом; грунтовка, гриф и HITL — в Ok-конверте")]
    public async Task Generates_draft_with_grounding_and_hitl()
    {
        var access = new AccessContext("u1", MaxClassification: 1, AllowedDivisions: [7]);
        var accessProvider = Substitute.For<IAccessContextProvider>();
        accessProvider.GetCurrentAsync(Arg.Any<CancellationToken>()).Returns(access);

        var renderer = Substitute.For<IReferencePromptRenderer>();
        renderer.Render(Arg.Any<GenerateReferenceCommand>()).Returns("ЗАДАЧНЫЙ ПРОМПТ");

        var grounding = new GroundingResult(
            [
                new CitationCheck("Закон N1", CitationStatus.Confirmed, 10),
                new CitationCheck("Закон N99", CitationStatus.Unverified),
            ],
            AllConfirmed: false);

        GroundedRequest? captured = null;
        var generator = Substitute.For<IGroundedGenerator>();
        generator.GenerateAsync(Arg.Do<GroundedRequest>(r => captured = r), access, Arg.Any<CancellationToken>())
            .Returns(new GroundedResponse("текст справки", grounding, [], ResultClassification: 1));

        var response = await new GenerateReferenceCommand.Handler(generator, accessProvider, renderer)
            .Handle(new GenerateReferenceCommand("режим хранения ДСП"), CancellationToken.None);

        // Запрос ушёл в оркестратор: тема — для извлечения, задачный промпт — из рендерера.
        captured.ShouldNotBeNull();
        captured!.Query.ShouldBe("режим хранения ДСП");
        captured.TaskPrompt.ShouldBe("ЗАДАЧНЫЙ ПРОМПТ");

        // Конверт: Ok + полезная нагрузка с черновиком, HITL, грунтовкой и грифом.
        response.Status.ShouldBeTrue();
        response.StatusCode.ShouldBe(ResponseStatusCode.Ok);
        response.Data.ShouldNotBeNull();
        response.Data!.DraftText.ShouldBe("текст справки");
        response.Data.RequiresHumanReview.ShouldBeTrue();
        response.Data.AllCitationsConfirmed.ShouldBeFalse();
        response.Data.Citations.Count.ShouldBe(2);
        response.Data.ResultClassification.ShouldBe<short>(1);
    }
}
