namespace ISC.AI.Abstractions.Grounding;

/// <summary>
/// Результат генерирующего сценария, несущий ВЕРДИКТ грунтовки (ТБ-040). Позволяет сквозному
/// <c>GroundingBehavior</c> убедиться, что грунтовка не обойдена, и пометить непроверенный вывод,
/// не завися от конкретного доменного типа результата.
/// </summary>
public interface IGroundedResult
{
    /// <summary>Все ссылки подтверждены грунтовкой; иначе вывод не «готов» и помечается (GATE-2).</summary>
    bool AllCitationsConfirmed { get; }
}
