using ISC.AI.Abstractions.AI;
using Scriban;

namespace ISC.AI.Profile.Inspector.Application.Features.Analysis;

/// <summary>
/// Рендерер ЗАДАЧНОГО промпта сравнения НПА (ТФ-НПА-04). Системное правило грунтовки добавляет
/// ядро (ТБ-041) — здесь только задача профиля. Промпт — файл-шаблон (Scriban), НЕ хардкод.
/// </summary>
public interface ICompareNpaPromptRenderer
{
    /// <summary>Рендерит задачный промпт по команде.</summary>
    string Render(CompareNpaCommand command);
}

/// <summary>Реализация на Scriban: ключ «analysis-compare» через нейтральный <see cref="IPromptProvider"/>.</summary>
public sealed class ScribanCompareNpaPromptRenderer(IPromptProvider promptProvider) : ICompareNpaPromptRenderer
{
    private readonly Template _template = Template.Parse(promptProvider.GetTaskPrompt("analysis-compare").Text);

    /// <inheritdoc />
    public string Render(CompareNpaCommand command) =>
        _template.Render(new { topic = command.Topic });
}

/// <summary>
/// Рендерер ЗАДАЧНОГО промпта анализа документа (ТФ-НПА-03) — те же правила, что у сравнения.
/// </summary>
public interface IAnalyzeDocumentPromptRenderer
{
    /// <summary>Рендерит задачный промпт по команде.</summary>
    string Render(AnalyzeDocumentCommand command);
}

/// <summary>Реализация на Scriban: ключ «analysis-document» через нейтральный <see cref="IPromptProvider"/>.</summary>
public sealed class ScribanAnalyzeDocumentPromptRenderer(IPromptProvider promptProvider) : IAnalyzeDocumentPromptRenderer
{
    private readonly Template _template = Template.Parse(promptProvider.GetTaskPrompt("analysis-document").Text);

    /// <inheritdoc />
    public string Render(AnalyzeDocumentCommand command) =>
        _template.Render(new { document = command.Text });
}

/// <summary>
/// Рендерер ЗАДАЧНОГО промпта сверки проекта до подписания (ТФ-АНПА-02) — те же правила.
/// </summary>
public interface ICheckDraftPromptRenderer
{
    /// <summary>Рендерит задачный промпт по команде.</summary>
    string Render(CheckDraftCommand command);
}

/// <summary>Реализация на Scriban: ключ «draft-check» через нейтральный <see cref="IPromptProvider"/>.</summary>
public sealed class ScribanCheckDraftPromptRenderer(IPromptProvider promptProvider) : ICheckDraftPromptRenderer
{
    private readonly Template _template = Template.Parse(promptProvider.GetTaskPrompt("draft-check").Text);

    /// <inheritdoc />
    public string Render(CheckDraftCommand command) =>
        _template.Render(new { draft = command.Text });
}
