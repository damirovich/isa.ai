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
/// Сценарии «Анализ / Сравнение» (ТФ-НПА-03/04): роль — ВДУМЧИВАЯ Analysis (ADR-0011), фрагментов
/// для сравнения больше обычного, оба шаблона встроены, результат всегда HITL (ТБ-042).
/// </summary>
public sealed class AnalysisScenariosTests
{
    [Fact(DisplayName = "Шаблоны «analysis-compare» и «analysis-document» встроены и рендерят вход")]
    public void Analysis_templates_render()
    {
        var provider = new EmbeddedScribanPromptProvider(
            typeof(InspectorApplicationServiceCollectionExtensions).Assembly);

        var compare = new ScribanCompareNpaPromptRenderer(provider)
            .Render(new CompareNpaCommand("сроки регистрации документов"));
        compare.ShouldContain("сроки регистрации документов");
        compare.ShouldContain("ПРОТИВОРЕЧИЯ");
        compare.ShouldContain("ПРОБЕЛЫ");
        compare.ShouldNotContain("{{");

        var analyze = new ScribanAnalyzeDocumentPromptRenderer(provider)
            .Render(new AnalyzeDocumentCommand("Текст проекта приказа."));
        analyze.ShouldContain("Текст проекта приказа.");
        analyze.ShouldContain("ПРАВОВОЙ АНАЛИЗ");
        analyze.ShouldNotContain("{{");
    }

    [Fact(DisplayName = "Сравнение: роль Analysis (вдумчивая), TopK расширен, результат — HITL")]
    public async Task Compare_uses_thinking_role_and_wide_retrieval()
    {
        var (generator, accessProvider, captured) = Substitutes("текст записки");
        var renderer = Substitute.For<ICompareNpaPromptRenderer>();
        renderer.Render(Arg.Any<CompareNpaCommand>()).Returns("ПРОМПТ СРАВНЕНИЯ");

        var response = await new CompareNpaCommand.Handler(generator, accessProvider, renderer)
            .Handle(new CompareNpaCommand("сроки регистрации"), CancellationToken.None);

        captured.Value.ShouldNotBeNull();
        captured.Value!.Role.ShouldBe(ModelRole.Analysis);
        captured.Value.TopK.ShouldBe(CompareNpaCommand.CompareTopK);
        captured.Value.Query.ShouldBe("сроки регистрации");
        response.Data!.RequiresHumanReview.ShouldBeTrue();
    }

    [Fact(DisplayName = "Анализ документа: тема извлечения — начало документа; роль Analysis")]
    public async Task Analyze_builds_retrieval_from_document_head()
    {
        var (generator, accessProvider, captured) = Substitutes("заключение");
        var renderer = Substitute.For<IAnalyzeDocumentPromptRenderer>();
        renderer.Render(Arg.Any<AnalyzeDocumentCommand>()).Returns("ПРОМПТ АНАЛИЗА");

        var longText = new string('б', AnalyzeDocumentCommand.RetrievalContextLength + 500);
        var response = await new AnalyzeDocumentCommand.Handler(generator, accessProvider, renderer)
            .Handle(new AnalyzeDocumentCommand(longText), CancellationToken.None);

        captured.Value.ShouldNotBeNull();
        captured.Value!.Role.ShouldBe(ModelRole.Analysis);
        captured.Value.Query.Length.ShouldBeLessThan(AnalyzeDocumentCommand.RetrievalContextLength + 100);
        response.Data!.DraftText.ShouldBe("заключение");
    }

    private static (IGroundedGenerator Generator, IAccessContextProvider Access, StrongBox<GroundedRequest?> Captured)
        Substitutes(string answer)
    {
        var access = new AccessContext("u1", MaxClassification: 0, AllowedDivisions: [7]);
        var accessProvider = Substitute.For<IAccessContextProvider>();
        accessProvider.GetCurrentAsync(Arg.Any<CancellationToken>()).Returns(access);

        var captured = new StrongBox<GroundedRequest?>();
        var generator = Substitute.For<IGroundedGenerator>();
        generator.GenerateAsync(Arg.Do<GroundedRequest>(r => captured.Value = r), access, Arg.Any<CancellationToken>())
            .Returns(new GroundedResponse(
                answer, new GroundingResult([], AllConfirmed: true), [], ResultClassification: 0));
        return (generator, accessProvider, captured);
    }

    private sealed class StrongBox<T>
    {
        public T? Value { get; set; }
    }
}
