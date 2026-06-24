namespace ISC.AI.Abstractions.Grounding;

/// <summary>Результат проверки одной ссылки/цитаты вывода модели (ТБ-040).</summary>
/// <param name="Citation">Текст ссылки/цитаты из вывода модели.</param>
/// <param name="Status">Статус проверки.</param>
/// <param name="SourceChunkId">Идентификатор фрагмента-источника, если ссылка сопоставлена.</param>
public sealed record CitationCheck(string Citation, CitationStatus Status, int? SourceChunkId = null);
