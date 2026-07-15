using ISC.AI.Abstractions.Grounding;

namespace ISC.AI.Profile.Inspector.Application.Generation;

/// <summary>Результат генерации справки (ТФ-ГЕН-01/04).</summary>
/// <param name="DraftText">Текст черновика справки.</param>
/// <param name="RequiresHumanReview">Всегда <c>true</c>: результат — проект, требующий проверки человеком (HITL, ТБ-042/ТЭ-002).</param>
/// <param name="AllCitationsConfirmed">Все ссылки подтверждены грунтовкой; иначе вывод не «готов» (GATE-2).</param>
/// <param name="Citations">Проверки ссылок: подтверждена / не подтверждена / утратила силу (ТБ-040).</param>
/// <param name="ResultClassification">Итоговый гриф = максимум грифов использованных фрагментов (ТБ-032/033).</param>
public sealed record GenerateReferenceResult(
    string DraftText,
    bool RequiresHumanReview,
    bool AllCitationsConfirmed,
    IReadOnlyList<CitationCheck> Citations,
    short ResultClassification) : IGroundedResult;
