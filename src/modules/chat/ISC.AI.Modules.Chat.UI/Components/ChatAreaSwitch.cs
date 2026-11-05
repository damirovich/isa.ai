namespace ISC.AI.Modules.Chat.UI;

/// <summary>
/// Переход в другую область чата с вопросом (ADR-0067): подсказка «вопрос не туда» в панели предлагает спросить там, где
/// ответят как нужно, — страница меняет область, и новая панель сразу задаёт этот вопрос.
/// </summary>
/// <param name="AreaKey">Область назначения; <see langword="null"/> — область по умолчанию.</param>
/// <param name="Question">Вопрос, который задать в новой области.</param>
public sealed record ChatAreaSwitch(string? AreaKey, string Question);
