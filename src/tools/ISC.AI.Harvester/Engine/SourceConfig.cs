namespace ISC.AI.Harvester.Engine;

/// <summary>
/// Настройка сбора из источника (задаётся ополнителем-оператором). Режимные поля
/// (<see cref="Classification"/>/<see cref="DivisionId"/>) декларируются здесь — не определяются авто.
/// </summary>
/// <param name="SeedUrl">Стартовый URL источника (список/страница/ссылка).</param>
/// <param name="DocType">Тип, присваиваемый собранным документам (свободная строка).</param>
/// <param name="Classification">Гриф для собранных документов (открытый корпус — 0).</param>
/// <param name="DivisionId">Подразделение для собранных документов.</param>
/// <param name="MaxDocuments">Верхний предел числа документов за прогон (вежливость к источнику).</param>
/// <param name="Language">Язык по умолчанию (ru/ky), если источник одноязычный.</param>
/// <param name="Rules">Правила-селекторы для <c>ConfigurableSiteConnector</c>; generic-коннектор их игнорирует.</param>
public sealed record SourceConfig(
    string SeedUrl,
    string DocType,
    short Classification,
    int DivisionId,
    int MaxDocuments = 50,
    string? Language = null,
    SiteRules? Rules = null);
