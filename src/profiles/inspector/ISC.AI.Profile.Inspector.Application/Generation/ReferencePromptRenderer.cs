using ISC.AI.Abstractions.AI;
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

/// <summary>
/// Реализация на Scriban: шаблон грузится по ключу «reference» через нейтральный
/// <see cref="IPromptProvider"/> (ТО-прог-04, ТО-лнг-03), а не прямым чтением ресурса.
/// </summary>
public sealed class ScribanReferencePromptRenderer(IPromptProvider promptProvider) : IReferencePromptRenderer
{
    private readonly Template _template = Template.Parse(promptProvider.GetTaskPrompt("reference").Text);

    /// <inheritdoc />
    public string Render(GenerateReferenceCommand command) =>
        _template.Render(new { topic = command.Topic });
}
