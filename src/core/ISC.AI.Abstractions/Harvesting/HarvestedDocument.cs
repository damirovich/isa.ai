namespace ISC.AI.Abstractions.Harvesting;

/// <summary>
/// Документ, собранный сборщиком ВНЕ контура (ADR-0015) — единица переносимого пакета импорта.
/// Общий контракт «через зазор»: его же читает импортёр в контуре, маппя в загрузку корпуса.
/// Типонезависим: тип — свободная строка <see cref="DocType"/>, источник-специфика — в открытом
/// <see cref="Metadata"/>. Режимные поля декларируются (открытый гриф; подразделение — из конфига оператора).
/// </summary>
/// <param name="SourceUrl">Исходный URL/идентификатор источника.</param>
/// <param name="Title">Заголовок документа.</param>
/// <param name="Text">Извлечённый текст.</param>
/// <param name="DocType">Тип документа (свободная строка: «закон», «положение», «web»…).</param>
/// <param name="ContentHash">Хеш содержимого (для дедупа/идемпотентности при импорте).</param>
/// <param name="Classification">Гриф (для открытого корпуса — 0; декларируется, не авто-детект).</param>
/// <param name="DivisionId">Подразделение (из конфига оператора).</param>
/// <param name="Language">Язык (ru/ky/…), если определён.</param>
/// <param name="Metadata">Источник-специфичные поля (номер/дата/статус редакции и т. п.).</param>
public sealed record HarvestedDocument(
    string SourceUrl,
    string Title,
    string Text,
    string DocType,
    string ContentHash,
    short Classification,
    int DivisionId,
    string? Language = null,
    IReadOnlyDictionary<string, string>? Metadata = null);
