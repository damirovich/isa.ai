using System.Reflection;
using Scriban;

namespace ISC.AI.AI.Chat;

/// <summary>
/// Системное правило СВОБОДНОГО режима чата: доброжелательное общение и помощь, но БЕЗ юридических утверждений
/// и ссылок на НПА «по памяти» — для точных норм и документов правило предлагает переключиться в грунтованный
/// режим. Файл-шаблон Scriban (ТО-лнг-03), встроенный ресурс (Chat/Prompts/conversational-system-rule.scriban).
/// </summary>
public static class ConversationalPrompt
{
    private static readonly Lazy<string> RenderedSystemRule = new(LoadSystemRule);

    /// <summary>Текст системного правила свободного режима (ставится первым system-сообщением).</summary>
    public static string SystemRule => RenderedSystemRule.Value;

    private static string LoadSystemRule()
    {
        var assembly = typeof(ConversationalPrompt).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .Single(name => name.EndsWith("conversational-system-rule.scriban", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resourceName)!;
        using var reader = new StreamReader(stream);
        return Template.Parse(reader.ReadToEnd()).Render();
    }
}
