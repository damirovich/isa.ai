using System.Threading;
using System.Threading.Tasks;
using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Grounding;
using ISC.AI.Abstractions.Rag;
using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Inspector.Application;
using ISC.AI.Profile.Inspector.Application.Features.Generation;
using ISC.AI.Profile.Inspector.Application.Features.Methods;
using NSubstitute;
using Shouldly;
using Xunit;

namespace ISC.AI.UnitTests.Profiles;

/// <summary>
/// Генерация методических документов (ТФ-МЕТ-01, §5.2.9): второй сценарий на конвейере Генератора.
/// Проверяется: настоящий шаблон «method» загружается и рендерит все параметры; handler отдаёт
/// оркестратору тему извлечения и задачный промпт; результат всегда HITL (ТБ-042).
/// </summary>
public sealed class MethodGenerationTests
{
    [Fact(DisplayName = "Шаблон «method» встроен и рендерит вид, тип, объект и доп. указания")]
    public void Method_template_renders_all_parameters()
    {
        // Настоящий провайдер по настоящей сборке — ловит опечатку в имени файла-ресурса.
        var provider = new EmbeddedScribanPromptProvider(
            typeof(InspectorApplicationServiceCollectionExtensions).Assembly);
        var renderer = new ScribanMethodPromptRenderer(provider);

        var prompt = renderer.Render(new GenerateMethodDocumentCommand(
            "Чек-лист", "Целевая", "Нарынская инспекция — режим делопроизводства", "учесть итоги 2025 года"));

        prompt.ShouldContain("Чек-лист");
        prompt.ShouldContain("Целевая");
        prompt.ShouldContain("Нарынская инспекция — режим делопроизводства");
        prompt.ShouldContain("учесть итоги 2025 года");
        prompt.ShouldContain("ИСКЛЮЧИТЕЛЬНО"); // Требование опоры только на фрагменты — в задачном промпте.
    }

    [Fact(DisplayName = "Без доп. указаний блок «Дополнительные указания» не рендерится")]
    public void Empty_extra_is_omitted()
    {
        var provider = new EmbeddedScribanPromptProvider(
            typeof(InspectorApplicationServiceCollectionExtensions).Assembly);
        var renderer = new ScribanMethodPromptRenderer(provider);

        var prompt = renderer.Render(new GenerateMethodDocumentCommand("Памятка", "Плановая", "ГИ", "  "));

        prompt.ShouldNotContain("Дополнительные указания");
    }

    [Fact(DisplayName = "Методика: оркестратор получает тему извлечения и задачный промпт; результат — HITL")]
    public async Task Handler_delegates_to_grounded_generator()
    {
        var access = new AccessContext("u1", MaxClassification: 1, AllowedDivisions: [7]);
        var accessProvider = Substitute.For<IAccessContextProvider>();
        accessProvider.GetCurrentAsync(Arg.Any<CancellationToken>()).Returns(access);

        var renderer = Substitute.For<IMethodPromptRenderer>();
        renderer.Render(Arg.Any<GenerateMethodDocumentCommand>()).Returns("ЗАДАЧНЫЙ ПРОМПТ МЕТОДИКИ");

        var grounding = new GroundingResult(
            [new CitationCheck("Приказ N1", CitationStatus.Confirmed, 3)], AllConfirmed: true);

        GroundedRequest? captured = null;
        var generator = Substitute.For<IGroundedGenerator>();
        generator.GenerateAsync(Arg.Do<GroundedRequest>(r => captured = r), access, Arg.Any<CancellationToken>())
            .Returns(new GroundedResponse("текст программы", grounding, [], ResultClassification: 1));

        var response = await new GenerateMethodDocumentCommand.Handler(generator, accessProvider, renderer)
            .Handle(new GenerateMethodDocumentCommand(
                "Программа проверки", "Комплексная", "Нарынская инспекция", "акцент на сроки"),
                CancellationToken.None);

        captured.ShouldNotBeNull();
        captured!.Query.ShouldContain("Комплексная");
        captured.Query.ShouldContain("Нарынская инспекция");
        captured.Query.ShouldContain("акцент на сроки");
        captured.TaskPrompt.ShouldBe("ЗАДАЧНЫЙ ПРОМПТ МЕТОДИКИ");

        response.Status.ShouldBeTrue();
        response.Data.ShouldNotBeNull();
        response.Data!.DraftText.ShouldBe("текст программы");
        response.Data.RequiresHumanReview.ShouldBeTrue();
        response.Data.AllCitationsConfirmed.ShouldBeTrue();
        response.Data.ResultClassification.ShouldBe<short>(1);
    }

    [Fact(DisplayName = "Валидатор: пустой объект проверки — отказ; заполненный минимум — принят")]
    public void Validator_requires_scope()
    {
        var validator = new GenerateMethodDocumentValidator();

        validator.Validate(new GenerateMethodDocumentCommand("Чек-лист", "Целевая", "")).IsValid.ShouldBeFalse();
        validator.Validate(new GenerateMethodDocumentCommand("", "Целевая", "ГИ")).IsValid.ShouldBeFalse();
        validator.Validate(new GenerateMethodDocumentCommand("Чек-лист", "Целевая", "ГИ")).IsValid.ShouldBeTrue();
    }
}
