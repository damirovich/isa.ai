namespace ISC.AI.Abstractions.AI;

/// <summary>
/// Task-префиксы модели эмбеддингов EmbeddingGemma (ADR-0011, справочник
/// <c>docs/reference/llm-api-embeddinggemma.md</c> §3, §12).
/// </summary>
/// <remarks>
/// EmbeddingGemma обучена с <b>асимметричным</b> кодированием: поисковый <b>запрос</b> и индексируемый
/// <b>документ</b> кодируются по-разному. Без префиксов качество retrieval заметно падает («не опционально»),
/// поэтому запрос и чанк перед эмбеддингом оборачиваются СВОИМ префиксом. Применяется в двух точках:
/// ретривер (<c>PgVectorRetriever</c>) — к запросу, ingestion (<c>IngestionPort</c>) — к каждому чанку;
/// обе стороны обязаны использовать одну и ту же модель/сервер (Э4-09).
/// <para>
/// Префиксы специфичны для EmbeddingGemma. При смене эмбеддера их нужно пересмотреть (вынести за конфиг/абстракцию).
/// </para>
/// </remarks>
public static class EmbeddingTaskPrompt
{
    /// <summary>Оборачивает поисковый <b>запрос</b> префиксом задачи retrieval.</summary>
    /// <param name="text">Текст запроса пользователя.</param>
    /// <returns>Строка вида <c>task: search result | query: {text}</c> для эмбеддинга.</returns>
    public static string Query(string text) => $"task: search result | query: {text}";

    /// <summary>Оборачивает индексируемый <b>документ/чанк</b> document-префиксом (заголовок не выделяем — <c>none</c>).</summary>
    /// <param name="text">Текст чанка корпуса.</param>
    /// <returns>Строка вида <c>title: none | text: {text}</c> для эмбеддинга.</returns>
    public static string Document(string text) => $"title: none | text: {text}";
}
