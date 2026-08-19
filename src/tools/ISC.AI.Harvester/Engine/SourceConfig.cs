namespace ISC.AI.Harvester.Engine;

/// <summary>
/// Настройка сбора из источника (задаётся ополнителем-оператором). Гриф (<see cref="Classification"/>)
/// декларируется здесь — не определяется авто. ПОДРАЗДЕЛЕНИЯ здесь НЕТ намеренно (2026-08-19): сборщик
/// работает вне контура и справочника подразделений не видит — введённый наобум номер (случай «10»)
/// клал материал под несуществующее подразделение, и его не видел никто. Подразделение выбирает
/// оператор при ИМПОРТЕ пакета внутри контура, из справочника; в пакете поле пустое и без выбора
/// пакет не загрузится (fail-closed, ТБ-024).
/// </summary>
/// <param name="SeedUrl">Стартовый URL источника (список/страница/ссылка).</param>
/// <param name="DocType">Тип, присваиваемый собранным документам (свободная строка).</param>
/// <param name="Classification">Гриф для собранных документов (открытый корпус — 0).</param>
/// <param name="MaxDocuments">Верхний предел числа документов за прогон (вежливость к источнику).</param>
/// <param name="Language">Язык по умолчанию (ru/ky), если источник одноязычный.</param>
/// <param name="Rules">Правила-селекторы для <c>ConfigurableSiteConnector</c>; generic-коннектор их игнорирует.</param>
/// <param name="StartPage">Стартовая страница пагинации (для возобновления массового сбора, Э4-17). 1 — с начала.</param>
public sealed record SourceConfig(
    string SeedUrl,
    string DocType,
    short Classification,
    int MaxDocuments = 50,
    string? Language = null,
    SiteRules? Rules = null,
    int StartPage = 1);
