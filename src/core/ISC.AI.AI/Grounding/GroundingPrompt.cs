using System.Reflection;
using Scriban;

namespace ISC.AI.AI.Grounding;

/// <summary>
/// Неотключаемое системное правило грунтовки (ТБ-041, ADR-0005). Задаётся ЯДРОМ; профиль НЕ может его
/// переопределить или ослабить. Оркестратор (Э3-10) ставит это правило ПЕРВЫМ системным сообщением;
/// задачные промпты профиля идут после и не отменяют его (порядок: системный ядра + задачный профиля + данные).
/// </summary>
public static class GroundingPrompt
{
    // Текст — файл-шаблон Scriban (ТО-лнг-03: "промпты хранятся файлами-шаблонами... подставляются
    // шаблонизатором"), встроенный ресурс сборки (см. Grounding/Prompts/grounding-system-rule.scriban).
    // Переменных в этом правиле нет, но грузится тем же путём, что и задачные промпты профиля
    // (ScribanReferencePromptRenderer) — единообразие важнее, чем экономия на Template.Render() без модели.
    private static readonly Lazy<string> RenderedSystemRule = new(LoadSystemRule);

    /// <summary>Текст системного правила грунтовки (ставится первым system-сообщением).</summary>
    public static string SystemRule => RenderedSystemRule.Value;

    private static string LoadSystemRule()
    {
        var assembly = typeof(GroundingPrompt).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .Single(name => name.EndsWith("grounding-system-rule.scriban", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resourceName)!;
        using var reader = new StreamReader(stream);
        return Template.Parse(reader.ReadToEnd()).Render();
    }
}
