using System.Threading;
using System.Threading.Tasks;
using ISC.AI.Abstractions.Grounding;
using ISC.AI.Abstractions.Rag;
using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Inspector.Application;
using ISC.AI.Profile.Inspector.Application.Features.Editor;
using ISC.AI.Profile.Inspector.Application.Features.Generation;
using NSubstitute;
using Shouldly;
using Xunit;

namespace ISC.AI.UnitTests.Profiles;

/// <summary>
/// ИИ-правка Редактора (ТФ-РЕД-02, §5.2.10): третий сценарий на конвейере Генератора. Проверяется:
/// настоящий шаблон «editor» встроен и рендерит команду с документом; handler отдаёт оркестратору
/// тему извлечения (команда + НАЧАЛО документа, не весь) и задачный промпт; результат всегда HITL.
/// </summary>
public sealed class EditorRevisionTests
{
    [Fact(DisplayName = "Шаблон «editor» встроен и рендерит команду и документ")]
    public void Editor_template_renders_instruction_and_document()
    {
        // Настоящий провайдер по настоящей сборке — ловит опечатку в имени файла-ресурса.
        var provider = new EmbeddedScribanPromptProvider(
            typeof(InspectorApplicationServiceCollectionExtensions).Assembly);
        var renderer = new ScribanEditorPromptRenderer(provider);

        var prompt = renderer.Render(new ReviseDocumentCommand(
            "Текст программы проверки.", "переформулируй строже"));

        prompt.ShouldContain("переформулируй строже");
        prompt.ShouldContain("Текст программы проверки.");
        prompt.ShouldContain("ИСКЛЮЧИТЕЛЬНО"); // Опора только на фрагменты — в задачном промпте.
        prompt.ShouldNotContain("{{"); // Все подстановки отработали, служебный заголовок съеден.
    }

    [Fact(DisplayName = "Правка: тема извлечения — команда + начало документа; результат — HITL")]
    public async Task Handler_builds_retrieval_query_from_instruction_and_context()
    {
        var access = new AccessContext("u1", MaxClassification: 1, AllowedDivisions: [7]);
        var accessProvider = Substitute.For<IAccessContextProvider>();
        accessProvider.GetCurrentAsync(Arg.Any<CancellationToken>()).Returns(access);

        var renderer = Substitute.For<IEditorPromptRenderer>();
        renderer.Render(Arg.Any<ReviseDocumentCommand>()).Returns("ЗАДАЧНЫЙ ПРОМПТ ПРАВКИ");

        var grounding = new GroundingResult(
            [new CitationCheck("Приказ N1", CitationStatus.Confirmed, 3)], AllConfirmed: true);

        GroundedRequest? captured = null;
        var generator = Substitute.For<IGroundedGenerator>();
        generator.GenerateAsync(Arg.Do<GroundedRequest>(r => captured = r), access, Arg.Any<CancellationToken>())
            .Returns(new GroundedResponse("новая редакция", grounding, [], ResultClassification: 1));

        // Документ длиннее предела контекста извлечения — в Query уходит только начало.
        var longText = new string('а', ReviseDocumentCommand.RetrievalContextLength + 500);
        var response = await new ReviseDocumentCommand.Handler(generator, accessProvider, renderer)
            .Handle(new ReviseDocumentCommand(longText, "добавь норму про сроки"), CancellationToken.None);

        captured.ShouldNotBeNull();
        captured!.Query.ShouldStartWith("добавь норму про сроки");
        captured.Query.Length.ShouldBeLessThan(ReviseDocumentCommand.RetrievalContextLength + 100);
        captured.TaskPrompt.ShouldBe("ЗАДАЧНЫЙ ПРОМПТ ПРАВКИ");

        response.Status.ShouldBeTrue();
        response.Data.ShouldNotBeNull();
        response.Data!.DraftText.ShouldBe("новая редакция");
        response.Data.RequiresHumanReview.ShouldBeTrue();
        response.Data.AllCitationsConfirmed.ShouldBeTrue();
    }

    [Fact(DisplayName = "Пустой ответ модели — честный отказ, а не пустая редакция (текст не затирается)")]
    public async Task Empty_model_answer_is_rejected()
    {
        var access = new AccessContext("u1", MaxClassification: 0, AllowedDivisions: [7]);
        var accessProvider = Substitute.For<IAccessContextProvider>();
        accessProvider.GetCurrentAsync(Arg.Any<CancellationToken>()).Returns(access);

        var renderer = Substitute.For<IEditorPromptRenderer>();
        renderer.Render(Arg.Any<ReviseDocumentCommand>()).Returns("ПРОМПТ");

        // «Думающая» модель потратила весь лимит на размышления: ответ пуст.
        var generator = Substitute.For<IGroundedGenerator>();
        generator.GenerateAsync(Arg.Any<GroundedRequest>(), access, Arg.Any<CancellationToken>())
            .Returns(new GroundedResponse("", new GroundingResult([], AllConfirmed: true), [], 0));

        var response = await new ReviseDocumentCommand.Handler(generator, accessProvider, renderer)
            .Handle(new ReviseDocumentCommand("исходный текст", "строже"), CancellationToken.None);

        response.Status.ShouldBeFalse();
        response.StatusMessage.ShouldNotBeNull();
        response.StatusMessage.ShouldContain("пустой ответ");
    }

    [Fact(DisplayName = "Валидатор: пустой текст или пустая команда — отказ")]
    public void Validator_requires_text_and_instruction()
    {
        var validator = new ReviseDocumentValidator();

        validator.Validate(new ReviseDocumentCommand("", "строже")).IsValid.ShouldBeFalse();
        validator.Validate(new ReviseDocumentCommand("текст", "")).IsValid.ShouldBeFalse();
        validator.Validate(new ReviseDocumentCommand("текст", "строже")).IsValid.ShouldBeTrue();
    }
}
