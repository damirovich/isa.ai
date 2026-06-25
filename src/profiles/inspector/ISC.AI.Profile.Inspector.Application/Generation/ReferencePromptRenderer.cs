using Scriban;

namespace ISC.AI.Profile.Inspector.Application.Generation;

/// <summary>
/// Рендерер ЗАДАЧНОГО промпта справки. Системное правило грунтовки добавляет ядро (ТБ-041) —
/// здесь только задача профиля. Промпт — файл-шаблон (Scriban), НЕ хардкод (правило проекта).
/// </summary>
public interface IReferencePromptRenderer
{
    /// <summary>Рендерит задачный промпт по команде.</summary>
    string Render(GenerateReferenceCommand command);
}

/// <summary>Реализация на Scriban: шаблон — встроенный ресурс <c>reference.scriban</c>.</summary>
public sealed class ScribanReferencePromptRenderer : IReferencePromptRenderer
{
    private readonly Template _template = Template.Parse(LoadTemplate());

    /// <inheritdoc />
    public string Render(GenerateReferenceCommand command) =>
        _template.Render(new { topic = command.Topic });

    private static string LoadTemplate()
    {
        var assembly = typeof(ScribanReferencePromptRenderer).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .Single(n => n.EndsWith("reference.scriban", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resourceName)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
