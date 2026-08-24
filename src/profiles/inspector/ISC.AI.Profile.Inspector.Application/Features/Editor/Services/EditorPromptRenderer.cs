using ISC.AI.Abstractions.AI;
using Scriban;

namespace ISC.AI.Profile.Inspector.Application.Features.Editor;

/// <summary>
/// Рендерер ЗАДАЧНОГО промпта ИИ-правки (ТФ-РЕД-02). Системное правило грунтовки добавляет ядро
/// (ТБ-041) — здесь только задача профиля. Промпт — файл-шаблон (Scriban), НЕ хардкод.
/// </summary>
public interface IEditorPromptRenderer
{
    /// <summary>Рендерит задачный промпт по команде.</summary>
    string Render(ReviseDocumentCommand command);
}

/// <summary>
/// Реализация на Scriban: шаблон грузится по ключу «editor» через нейтральный
/// <see cref="IPromptProvider"/> (ТО-прог-04, ТО-лнг-03), а не прямым чтением ресурса.
/// </summary>
public sealed class ScribanEditorPromptRenderer(IPromptProvider promptProvider) : IEditorPromptRenderer
{
    private readonly Template _template = Template.Parse(promptProvider.GetTaskPrompt("editor").Text);

    /// <inheritdoc />
    public string Render(ReviseDocumentCommand command) =>
        _template.Render(new { instruction = command.Instruction, document = command.Text });
}
