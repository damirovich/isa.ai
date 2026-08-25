using ISC.AI.Abstractions.AI;
using Scriban;

namespace ISC.AI.Profile.Inspector.Application.Features.Generation;

/// <summary>
/// Рендерер ЗАДАЧНОГО промпта Генератора: жанр и структуру задаёт шаблон ТИПА документа
/// (ТФ-ГЕН-01, перечень — <see cref="ReferenceDocumentTypes"/>). Системное правило грунтовки
/// добавляет ядро (ТБ-041) — здесь только задача профиля. Промпты — файлы-шаблоны (Scriban),
/// НЕ хардкод (правило проекта).
/// </summary>
public interface IReferencePromptRenderer
{
    /// <summary>Рендерит задачный промпт по команде (шаблон — по её типу документа).</summary>
    string Render(GenerateReferenceCommand command);
}

/// <summary>
/// Реализация на Scriban: шаблоны грузятся по ключам через нейтральный
/// <see cref="IPromptProvider"/> (ТО-прог-04, ТО-лнг-03), а не прямым чтением ресурса.
/// Все типы парсятся при создании — опечатка в имени ресурса всплывает на первом
/// обращении к Генератору, а не при редком выборе конкретного типа.
/// </summary>
public sealed class ScribanReferencePromptRenderer(IPromptProvider promptProvider) : IReferencePromptRenderer
{
    private readonly IReadOnlyDictionary<string, Template> _templates =
        ReferenceDocumentTypes.TemplateKeys.ToDictionary(
            pair => pair.Key,
            pair => Template.Parse(promptProvider.GetTaskPrompt(pair.Value).Text),
            StringComparer.Ordinal);

    /// <inheritdoc />
    public string Render(GenerateReferenceCommand command)
    {
        // Неизвестный тип сюда не доходит (валидатор), поэтому попадание — рассинхрон кода,
        // о котором надо кричать, а не молча подменять жанр справкой.
        if (!_templates.TryGetValue(command.DocumentType, out var template))
        {
            throw new InvalidOperationException(
                $"Для типа документа «{command.DocumentType}» нет промпт-шаблона "
                + "(перечень — ReferenceDocumentTypes).");
        }

        return template.Render(new { topic = command.Topic });
    }
}
