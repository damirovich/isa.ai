namespace ISC.AI.Harvester.Engine;

/// <summary>
/// Готовый пресет источника — это ДАННЫЕ-конфиг (правила + подсказки), а не класс-коннектор (ADR-0015).
/// Оператор выбирает пресет в UI, поля заполняются; селекторы можно поправить, если сайт сменил вёрстку.
/// </summary>
/// <param name="Name">Имя пресета для UI.</param>
/// <param name="SuggestedSeedUrl">Подсказка стартового URL.</param>
/// <param name="DocType">Тип документа по умолчанию.</param>
/// <param name="Rules">Правила извлечения (CSS-селекторы).</param>
public sealed record SitePreset(string Name, string SuggestedSeedUrl, string DocType, SiteRules Rules);
