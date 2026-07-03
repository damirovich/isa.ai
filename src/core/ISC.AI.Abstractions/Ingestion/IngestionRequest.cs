namespace ISC.AI.Abstractions.Ingestion;

/// <summary>
/// Запрос на загрузку документа в корпус. Гриф и подразделение — <see langword="null"/>-овые НАМЕРЕННО:
/// отсутствие значения означает «не задано явно» и ведёт к отказу (fail-closed, ТБ-024), а не к
/// индексации с открытым грифом по умолчанию.
/// </summary>
/// <param name="DocType">Тип документа (закон, приказ, положение, архивная справка…).</param>
/// <param name="Title">Наименование.</param>
/// <param name="Text">Извлечённый текст документа (OCR/парсинг форматов — на стороне источника / Э4-01).</param>
/// <param name="Classification">Гриф. <see langword="null"/> — не задан → отказ (ТБ-024).</param>
/// <param name="DivisionId">Подразделение. <see langword="null"/> — не задано → отказ (ТБ-024).</param>
/// <param name="Source">Источник/происхождение.</param>
/// <param name="StorageUri">Ссылка на исходный файл в защищённом хранилище.</param>
/// <param name="DocDate">Дата документа.</param>
/// <param name="Metadata">Доменный «багаж» профиля (для НПА — идентификатор нормы и т. п.).</param>
/// <param name="SupersedesDocumentId">Если это НОВАЯ версия — идентификатор заменяемого документа: его
/// чанки будут погашены (Э4-14). <see langword="null"/> — новый документ, ничего не заменяет.</param>
public sealed record IngestionRequest(
    string DocType,
    string Title,
    string Text,
    short? Classification,
    int? DivisionId,
    string? Source = null,
    string? StorageUri = null,
    DateOnly? DocDate = null,
    IReadOnlyDictionary<string, string>? Metadata = null,
    int? SupersedesDocumentId = null);
