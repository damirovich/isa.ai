using ISC.AI.Abstractions.AI;
using Shouldly;

namespace ISC.AI.UnitTests.Ai;

/// <summary>
/// Префиксы задач EmbeddingGemma (Э4-09): запрос и документ кодируются асимметрично, поэтому
/// оборачиваются разными префиксами перед эмбеддингом (справочник llm-api-embeddinggemma.md §3).
/// </summary>
public sealed class EmbeddingTaskPromptTests
{
    [Fact(DisplayName = "Запрос оборачивается query-префиксом retrieval")]
    public void Query_wraps_with_search_result_prefix()
    {
        EmbeddingTaskPrompt.Query("столица Кыргызстана")
            .ShouldBe("task: search result | query: столица Кыргызстана");
    }

    [Fact(DisplayName = "Чанк оборачивается document-префиксом (title: none)")]
    public void Document_wraps_with_title_text_prefix()
    {
        EmbeddingTaskPrompt.Document("текст чанка корпуса")
            .ShouldBe("title: none | text: текст чанка корпуса");
    }

    [Fact(DisplayName = "Префиксы запроса и документа различны (асимметрия EmbeddingGemma)")]
    public void Query_and_document_prefixes_differ()
    {
        const string text = "одинаковый текст";

        EmbeddingTaskPrompt.Query(text).ShouldNotBe(EmbeddingTaskPrompt.Document(text));
    }
}
