using System.Threading;
using System.Threading.Tasks;
using ISC.AI.Abstractions.Enums;
using ISC.AI.Abstractions.Grounding;
using ISC.AI.Abstractions.Rag;
using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Inspector.Application;
using ISC.AI.Profile.Inspector.Application.Features.Analysis;
using ISC.AI.Profile.Inspector.Application.Features.Generation;
using NSubstitute;
using Shouldly;
using Xunit;

namespace ISC.AI.UnitTests.Profiles;

/// <summary>
/// Сверка проекта до подписания (ТФ-АНПА-02): вдумчивая роль Analysis, расширенное извлечение
/// (охват базы, как у сравнения НПА), проект целиком — в задачном промпте, тема извлечения —
/// его начало. Заключение — проект для человека (ТБ-042); шаблон честно оговаривает пределы
/// сверки (в рамках фрагментов, не юр. экспертиза).
/// </summary>
public sealed class CheckDraftTests
{
    [Fact(DisplayName = "Сверка: роль Analysis, TopK как у сравнения, проект и оговорка — в промпте")]
    public async Task Handler_uses_analysis_role_and_wide_retrieval()
    {
        var access = new AccessContext("u1", MaxClassification: 0, AllowedDivisions: [1]);
        var accessProvider = Substitute.For<IAccessContextProvider>();
        accessProvider.GetCurrentAsync(Arg.Any<CancellationToken>()).Returns(access);

        GroundedRequest? captured = null;
        var generator = Substitute.For<IGroundedGenerator>();
        generator.GenerateAsync(Arg.Do<GroundedRequest>(r => captured = r), access, Arg.Any<CancellationToken>())
            .Returns(new GroundedResponse(
                "заключение сверки", new GroundingResult([], AllConfirmed: true), [], ResultClassification: 0));

        // Настоящий провайдер шаблонов — ловит опечатку в имени ресурса «draft-check».
        var renderer = new ScribanCheckDraftPromptRenderer(new EmbeddedScribanPromptProvider(
            typeof(InspectorApplicationServiceCollectionExtensions).Assembly));

        var response = await new CheckDraftCommand.Handler(generator, accessProvider, renderer)
            .Handle(new CheckDraftCommand("ПРОЕКТ ПРИКАЗА о порядке хранения носителей"), CancellationToken.None);

        captured.ShouldNotBeNull();
        captured!.Role.ShouldBe(ModelRole.Analysis);
        captured.TopK.ShouldBe(CheckDraftCommand.CheckTopK);            // охват базы, не пара норм.
        captured.Query.ShouldContain("о порядке хранения носителей");   // тема извлечения — начало проекта.
        captured.TaskPrompt.ShouldNotBeNull();
        captured.TaskPrompt.ShouldContain("КОЛЛИЗИИ И ПРОТИВОРЕЧИЯ");   // структура — из шаблона,
        captured.TaskPrompt.ShouldContain("ПРОЕКТ ПРИКАЗА о порядке");  // проект — целиком в промпте,
        captured.TaskPrompt.ShouldContain("не заменяет");               // пределы сверки оговорены.
        captured.TaskPrompt.ShouldNotContain("{{");                     // подстановки отработали.

        response.Status.ShouldBeTrue();
        response.Data!.DraftText.ShouldBe("заключение сверки");
        response.Data.RequiresHumanReview.ShouldBeTrue();
    }
}
