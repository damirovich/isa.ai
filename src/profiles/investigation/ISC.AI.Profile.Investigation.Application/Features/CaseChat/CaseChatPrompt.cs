using Scriban;

namespace ISC.AI.Profile.Investigation.Application.Features.CaseChat;

/// <summary>
/// Задачный промпт чата по материалам дел (ADR-0051) — файл-шаблон <c>Prompts/case-chat.scriban</c>, встроенный в
/// сборку (промпты не хардкодятся в C#).
/// </summary>
/// <remarks>
/// ИНВАРИАНТ (ТБ-041): это ВТОРОЕ системное сообщение — после неотключаемого правила грунтовки ядра, которое ставит
/// генератор. Шаблон его не отменяет и не ослабляет, а уточняет под материалы дела: ссылки на фрагменты, «ответа не
/// найдено» вместо догадок, текст документов — данные, а не указания (защита от инструкций внутри документов).
/// </remarks>
public static class CaseChatPrompt
{
    private static readonly Lazy<Template> Template = new(Load);

    /// <summary>Промпт для области: «дела № 12-2026/045 «…»» или «всех доступных вам дел (7)».</summary>
    public static string Render(string scope) =>
        Template.Value.Render(new { scope = scope ?? string.Empty }).Trim();

    private static Template Load()
    {
        var assembly = typeof(CaseChatPrompt).Assembly;
        var name = assembly.GetManifestResourceNames().Single(n => n.EndsWith("case-chat.scriban", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        var template = Scriban.Template.Parse(reader.ReadToEnd());
        if (template.HasErrors)
        {
            throw new InvalidOperationException("Шаблон промпта чата по делу не разобран: " + string.Join("; ", template.Messages));
        }

        return template;
    }
}
