using ISC.AI.Profile.Inspector.Application.Generation;
using Shouldly;

namespace ISC.AI.UnitTests.Profiles;

/// <summary>
/// ТО-прог-04/ТО-лнг-03: задачные промпты грузятся ЧЕРЕЗ провайдер по ключу из файлов-шаблонов
/// <c>.scriban</c> (встроенные ресурсы профиля), а не хардкодом и не прямым чтением ресурса.
/// </summary>
public sealed class EmbeddedScribanPromptProviderTests
{
    private static readonly EmbeddedScribanPromptProvider Provider =
        new(typeof(ScribanReferencePromptRenderer).Assembly);

    [Fact(DisplayName = "ТО-прог-04: задачный промпт грузится по ключу из .scriban-ресурса")]
    public void Loads_task_prompt_by_key()
    {
        var template = Provider.GetTaskPrompt("reference");

        template.Key.ShouldBe("reference");
        template.Text.ShouldContain("{{ topic }}"); // reference.scriban — файл-шаблон Scriban, не хардкод
    }

    [Fact(DisplayName = "ТО-прог-04: отсутствующий ключ — исключение (не молчаливая подмена)")]
    public void Missing_key_throws()
    {
        Should.Throw<KeyNotFoundException>(() => Provider.GetTaskPrompt("нет-такого-ключа"));
    }
}
