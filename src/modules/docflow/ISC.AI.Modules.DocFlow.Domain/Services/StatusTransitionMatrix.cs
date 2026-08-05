using ISC.AI.Modules.DocFlow.Domain.Enums;

namespace ISC.AI.Modules.DocFlow.Domain.Services;

/// <summary>
/// Матрица допустимых переходов статуса назначения (ТЗ СКИД §4.5). Чистая доменная логика без
/// ввода-вывода; роли «кто может» проверяются отдельно (этап 6 Э4-35).
/// </summary>
/// <remarks>
/// Перенесена из СКИД (их решение DL-052) — матрица УЗКАЯ, строго по §4.5: широкое примечание §4.2
/// «возврат допускается из любого состояния» в СКИД было сознательно закрыто. «Просрочено» вручную
/// не назначается (только система по сроку) и вручную не покидается (только через продление срока —
/// §4.6, автоматический возврат «В работу»); «Снято с контроля» — финал.
/// </remarks>
public static class StatusTransitionMatrix
{
    private static readonly Dictionary<AssignmentStatus, AssignmentStatus[]> Transitions = new()
    {
        [AssignmentStatus.Registered] = [AssignmentStatus.InControl, AssignmentStatus.InProgress],
        [AssignmentStatus.InControl] = [AssignmentStatus.InProgress],
        [AssignmentStatus.InProgress] = [AssignmentStatus.Done, AssignmentStatus.PartiallyDone],
        [AssignmentStatus.PartiallyDone] = [AssignmentStatus.InProgress],
        [AssignmentStatus.Done] = [AssignmentStatus.InProgress, AssignmentStatus.Closed],
        [AssignmentStatus.Overdue] = [],
        [AssignmentStatus.Closed] = [],
    };

    /// <summary>Допустим ли ручной переход <paramref name="from"/> → <paramref name="to"/> (§4.5).</summary>
    public static bool IsTransitionAllowed(AssignmentStatus from, AssignmentStatus to) =>
        Transitions.TryGetValue(from, out var allowed) && Array.IndexOf(allowed, to) >= 0;

    /// <summary>Куда можно перейти из <paramref name="from"/> (для выпадающего списка UI).</summary>
    public static IReadOnlyList<AssignmentStatus> AllowedFrom(AssignmentStatus from) =>
        Transitions.TryGetValue(from, out var allowed) ? allowed : [];
}
