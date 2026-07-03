namespace ISC.AI.Abstractions.Ingestion;

/// <summary>
/// Запрос на загрузку документа из ФАЙЛА: текст извлекается из <paramref name="Content"/> по
/// <paramref name="FileName"/>, остальное — объявленные метаданные. Гриф/подразделение — обязательны
/// (fail-closed проверяется портом, ТБ-024): <see langword="null"/> → отказ.
/// </summary>
/// <param name="Content">Поток содержимого файла.</param>
/// <param name="FileName">Имя файла (по расширению выбирается извлекатель текста).</param>
/// <param name="DocType">Тип документа (закон, приказ, положение, архивная справка…).</param>
/// <param name="Classification">Гриф. <see langword="null"/> — не задан → отказ (ТБ-024).</param>
/// <param name="DivisionId">Подразделение. <see langword="null"/> — не задано → отказ (ТБ-024).</param>
/// <param name="Title">Наименование; если не задано — берётся из метаданных файла или имени файла.</param>
/// <param name="Source">Источник/происхождение; если не задан — имя файла.</param>
/// <param name="StorageUri">Ссылка на исходный файл в защищённом хранилище.</param>
/// <param name="DocDate">Дата документа.</param>
/// <param name="Metadata">Доменный «багаж» профиля (для НПА — идентификатор нормы и т. п.).</param>
/// <param name="SupersedesDocumentId">Если это новая версия — идентификатор заменяемого документа (Э4-14).</param>
public sealed record FileIngestionRequest(
    Stream Content,
    string FileName,
    string DocType,
    short? Classification,
    int? DivisionId,
    string? Title = null,
    string? Source = null,
    string? StorageUri = null,
    DateOnly? DocDate = null,
    IReadOnlyDictionary<string, string>? Metadata = null,
    int? SupersedesDocumentId = null);
