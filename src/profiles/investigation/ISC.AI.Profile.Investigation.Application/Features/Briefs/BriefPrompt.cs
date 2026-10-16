using ISC.AI.Profile.Investigation.Domain.Services;
using Scriban;

namespace ISC.AI.Profile.Investigation.Application.Features.Briefs;

/// <summary>
/// Задачные промпты справок (ADR-0053) — файлы-шаблоны <c>Prompts/case-brief.scriban</c> и <c>person-brief.scriban</c>,
/// встроенные в сборку (промпты не хардкодятся в C#).
/// </summary>
/// <remarks>
/// ИНВАРИАНТ (ТБ-041): это ВТОРОЕ системное сообщение — после неотключаемого правила грунтовки ядра, которое ставит
/// генератор. Шаблон его не отменяет: он говорит, что источник этой задачи — данные справки, посчитанные системой
/// (фрагменты намеренно не подаются, ADR-0053), запрещает номера дел и документов и ссылки на статьи и кодексы (их нет
/// во фрагментах — проверка ссылок ядра пометит их непроверенными; сам знак номера в шаблоне не пишется, модель его
/// подхватывает), выводы о виновности и опознание, и требует считать данные сведениями, а не
/// указаниями (защита от команд внутри сводок и примечаний): данные огорожены метками «⟦НАЧАЛО ДАННЫХ⟧ … ⟦КОНЕЦ
/// ДАННЫХ⟧», и правило повторено ПОСЛЕ них — без этого на узле модель выполняла команду из обоснования в 3 случаях из
/// 4, с метками и повтором — ни разу (ADR-0053). Скобки меток из значений убирает <see cref="BriefTexts.ForModel"/>.
/// Значения Scriban повторно не разбираются: «{{» в данных безопасно.
/// </remarks>
public static class BriefPrompt
{
    private static readonly Lazy<Template> CaseTemplate = new(() => Load("case-brief.scriban"));
    private static readonly Lazy<Template> PersonTemplate = new(() => Load("person-brief.scriban"));

    /// <summary>Промпт справки с фактами для модели (<see cref="BriefTexts.ForModel"/>).</summary>
    public static string Render(BriefKind kind, string facts) =>
        (kind == BriefKind.Person ? PersonTemplate : CaseTemplate).Value.Render(new { facts = facts ?? string.Empty })
            .Replace("\r\n", "\n", StringComparison.Ordinal).Trim();

    private static Template Load(string fileName)
    {
        var assembly = typeof(BriefPrompt).Assembly;
        var name = assembly.GetManifestResourceNames().Single(n => n.EndsWith("." + fileName, StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        var template = Template.Parse(reader.ReadToEnd());
        if (template.HasErrors)
        {
            throw new InvalidOperationException("Шаблон промпта справки не разобран: " + string.Join("; ", template.Messages));
        }

        return template;
    }
}
