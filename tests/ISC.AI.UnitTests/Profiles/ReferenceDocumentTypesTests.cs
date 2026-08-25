using System.Threading;
using System.Threading.Tasks;
using ISC.AI.Abstractions.Enums;
using ISC.AI.Abstractions.Grounding;
using ISC.AI.Abstractions.Rag;
using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Inspector.Application;
using ISC.AI.Profile.Inspector.Application.Features.Generation;
using NSubstitute;
using Shouldly;
using Xunit;

namespace ISC.AI.UnitTests.Profiles;

/// <summary>
/// Типы документов Генератора (ТФ-ГЕН-01, §5.2.1.1): у КАЖДОГО типа — свой встроенный
/// промпт-шаблон с жанровой структурой; перечень един (ReferenceDocumentTypes) — рассинхрон
/// «в селекте есть, шаблона нет» ловится здесь настоящим провайдером. Проект приказа
/// (ТФ-АНПА-01) дополнительно запрещает выдуманные реквизиты и требует норм-оснований.
/// </summary>
public sealed class ReferenceDocumentTypesTests
{
    private static ScribanReferencePromptRenderer CreateRenderer() =>
        new(new EmbeddedScribanPromptProvider(
            typeof(InspectorApplicationServiceCollectionExtensions).Assembly));

    [Theory(DisplayName = "Каждый тип рендерится своим шаблоном: жанровый заголовок и тема на месте")]
    [InlineData("Справка об итогах проверки", "справку")]
    [InlineData("Акт проверки", "АКТ проверки")]
    [InlineData("Рапорт", "РАПОРТ")]
    [InlineData("Докладная записка", "ДОКЛАДНАЯ ЗАПИСКА")]
    [InlineData("Заключение", "ЗАКЛЮЧЕНИЕ")]
    [InlineData("Проект приказа", "ПРИКАЗ №")]
    public void Each_type_renders_its_genre_template(string documentType, string genreMarker)
    {
        var prompt = CreateRenderer().Render(
            new GenerateReferenceCommand("проверка режима хранения", documentType));

        prompt.ShouldContain(genreMarker);                       // жанр — из шаблона типа,
        prompt.ShouldContain("проверка режима хранения");        // тема — подставлена,
        prompt.ShouldNotContain("{{");                           // подстановки отработали.
    }

    [Fact(DisplayName = "Проект приказа: норм-основания обязательны, реквизиты и сроки не выдумываются")]
    public void Draft_order_requires_grounded_basis()
    {
        var prompt = CreateRenderer().Render(new GenerateReferenceCommand("тема", "Проект приказа"));

        prompt.ShouldContain("КОНСТАТИРУЮЩАЯ ЧАСТЬ");
        prompt.ShouldContain("дословно из фрагментов");
        prompt.ShouldContain("в срок до ______");
    }

    [Fact(DisplayName = "Валидатор: неизвестный тип документа отклоняется")]
    public void Validator_rejects_unknown_type()
    {
        var result = new GenerateReferenceValidator()
            .Validate(new GenerateReferenceCommand("нормальная тема", "Небывалый тип"));

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.ErrorMessage == "Неизвестный тип документа.");
    }

    [Fact(DisplayName = "Тип по умолчанию — справка: старые вызовы работают без изменений")]
    public async Task Default_type_is_reference()
    {
        var access = new AccessContext("u1", MaxClassification: 0, AllowedDivisions: [1]);
        var accessProvider = Substitute.For<IAccessContextProvider>();
        accessProvider.GetCurrentAsync(Arg.Any<CancellationToken>()).Returns(access);

        GroundedRequest? captured = null;
        var generator = Substitute.For<IGroundedGenerator>();
        generator.GenerateAsync(Arg.Do<GroundedRequest>(r => captured = r), access, Arg.Any<CancellationToken>())
            .Returns(new GroundedResponse(
                "текст", new GroundingResult([], AllConfirmed: true), [], ResultClassification: 0));

        var response = await new GenerateReferenceCommand.Handler(
                generator, accessProvider, CreateRenderer())
            .Handle(new GenerateReferenceCommand("тема без типа"), CancellationToken.None);

        response.Status.ShouldBeTrue();
        captured!.Role.ShouldBe(ModelRole.Draft);
        captured.TaskPrompt.ShouldNotBeNull();
        captured.TaskPrompt.ShouldContain("справку"); // жанр по умолчанию — справка.
    }
}
