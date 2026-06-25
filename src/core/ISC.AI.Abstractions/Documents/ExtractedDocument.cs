namespace ISC.AI.Abstractions.Documents;

/// <summary>
/// Результат извлечения текста из файла документа. Нейтрален к типу документа (ADR-0013):
/// извлекатель не знает, НПА это, положение или архивная справка.
/// </summary>
/// <param name="Text">Извлечённый плоский текст (вход для чанкинга и эмбеддингов).</param>
/// <param name="Title">Заголовок из метаданных файла, если доступен.</param>
/// <param name="Language">Код языка (ru/ky/…), если определён извлекателем.</param>
public sealed record ExtractedDocument(string Text, string? Title = null, string? Language = null);
