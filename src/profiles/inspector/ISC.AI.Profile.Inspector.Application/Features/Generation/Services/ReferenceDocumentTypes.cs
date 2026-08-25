namespace ISC.AI.Profile.Inspector.Application.Features.Generation;

/// <summary>
/// Типы документов Генератора (ТФ-ГЕН-01, §5.2.1.1) и их промпт-шаблоны. ЕДИНСТВЕННЫЙ источник
/// перечня: UI, валидатор и рендерер смотрят сюда — рассинхрон «в селекте есть, шаблона нет»
/// невозможен по построению.
/// </summary>
public static class ReferenceDocumentTypes
{
    /// <summary>Справка об итогах проверки (P0-тип, исходный).</summary>
    public const string Reference = "Справка об итогах проверки";

    /// <summary>
    /// Тип → ключ промпт-шаблона (файл <c>Generation/Prompts/{ключ}.scriban</c>).
    /// «Проект приказа» закрывает и ТФ-АНПА-01 (распорядительный документ с норм-основаниями).
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> TemplateKeys =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [Reference] = "reference",
            ["Акт проверки"] = "act",
            ["Рапорт"] = "raport",
            ["Докладная записка"] = "memo",
            ["Заключение"] = "conclusion",
            ["Проект приказа"] = "draft-order",
        };

    /// <summary>Перечень типов в порядке показа в интерфейсе.</summary>
    public static IReadOnlyList<string> All { get; } = [.. TemplateKeys.Keys];
}
