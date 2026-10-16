namespace ISC.AI.Profile.Investigation.Application.Features.CaseChat;

/// <summary>
/// Подсказки — готовые вопросы чата по материалам (ADR-0051): видны всегда, щелчок задаёт вопрос сразу. Нужны на показе
/// и новому сотруднику: что вообще можно спросить. Подсказки — обычные вопросы, особой обработки у них нет. Пакет «Чат»
/// получает их через порт профиля (<see cref="InvestigationChatProfile"/>, ADR-0052).
/// </summary>
public static class CaseChatSuggestions
{
    /// <summary>Вопросы по всем доступным делам.</summary>
    public static IReadOnlyList<string> AllCases { get; } =
    [
        "Какие фигуранты есть в моих делах и какие у них машины?",
        "О чём говорили в записях?",
        "У кого есть автомобиль Toyota Camry?",
        "Какие адреса упоминаются в материалах?",
        "Кратко: что известно по каждому делу?",
    ];

    /// <summary>Вопросы по одному делу.</summary>
    public static IReadOnlyList<string> OneCase { get; } =
    [
        "Кратко: о чём это дело?",
        "Кто фигуранты и что о них известно?",
        "О чём говорили в записях?",
        "Какие адреса и машины упоминаются?",
        "Что известно о встречах фигурантов?",
    ];

    /// <summary>Запросы для поиска по смыслу — не точные слова, а смысл.</summary>
    public static IReadOnlyList<string> Sense { get; } =
    [
        "встреча на рынке",
        "передача денег",
        "автомобиль объекта",
        "телефонный разговор",
        "адрес проживания",
    ];

    /// <summary>Вопросы для области: одно дело или все дела.</summary>
    public static IReadOnlyList<string> For(int? caseId) => caseId is null ? AllCases : OneCase;
}
