using ISC.AI.Abstractions.Grounding;
using ISC.AI.Abstractions.Retrieval;

namespace ISC.AI.Abstractions.Rag;

/// <summary>Результат RAG-конвейера: ответ модели + проверка грунтовки + использованные фрагменты.</summary>
/// <param name="Answer">Текст ответа модели.</param>
/// <param name="Grounding">Результат грунтовки (ТБ-040): подтверждённость каждой ссылки.</param>
/// <param name="UsedFragments">Извлечённые фрагменты, поданные модели (для аудита и расчёта грифа).</param>
/// <param name="ResultClassification">
/// Итоговый гриф результата = МАКСИМУМ грифов использованных фрагментов (наследование грифа, ТБ-032/033).
/// </param>
public sealed record GroundedResponse(
    string Answer,
    GroundingResult Grounding,
    IReadOnlyList<RetrievedChunk> UsedFragments,
    short ResultClassification);
