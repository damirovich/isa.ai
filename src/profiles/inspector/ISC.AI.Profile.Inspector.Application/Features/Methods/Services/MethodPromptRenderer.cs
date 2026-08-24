using ISC.AI.Abstractions.AI;
using Scriban;

namespace ISC.AI.Profile.Inspector.Application.Features.Methods;

/// <summary>
/// Рендерер ЗАДАЧНОГО промпта методического документа (ТФ-МЕТ-01). Системное правило грунтовки
/// добавляет ядро (ТБ-041) — здесь только задача профиля. Промпт — файл-шаблон (Scriban), НЕ хардкод.
/// </summary>
public interface IMethodPromptRenderer
{
    /// <summary>Рендерит задачный промпт по команде.</summary>
    string Render(GenerateMethodDocumentCommand command);
}

/// <summary>
/// Реализация на Scriban: шаблон грузится по ключу «method» через нейтральный
/// <see cref="IPromptProvider"/> (ТО-прог-04, ТО-лнг-03), а не прямым чтением ресурса.
/// </summary>
public sealed class ScribanMethodPromptRenderer(IPromptProvider promptProvider) : IMethodPromptRenderer
{
    private readonly Template _template = Template.Parse(promptProvider.GetTaskPrompt("method").Text);

    /// <inheritdoc />
    public string Render(GenerateMethodDocumentCommand command) =>
        _template.Render(new
        {
            artifact_kind = command.ArtifactKind,
            inspection_type = command.InspectionType,
            scope = command.Scope,
            // null (а не пустая строка): в шаблоне блок «доп. указания» отрисовывается по if extra.
            extra = string.IsNullOrWhiteSpace(command.Extra) ? null : command.Extra.Trim(),
        });
}
