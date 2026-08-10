using ISC.AI.Modules.DocFlow.Domain.Enums;

namespace ISC.AI.Modules.DocFlow.Domain.Services;

/// <summary>
/// Расчёт агрегированного статуса документа по статусам всех его назначений (ТЗ СКИД §4.3).
/// Чистая функция без ввода-вывода — перенесена из СКИД дословно (совместимость поведения 1:1).
/// </summary>
/// <remarks>
/// Правило приоритетов (по убыванию):
/// 1) хотя бы одно «Просрочено» → Просрочено; 2) хотя бы одно «Частично исполнено» → Частично;
/// 3) хотя бы одно «В работе»/«Контроль» → В работе (Контроль приравнивается к работе);
/// 4) все «Исполнено» → Исполнено; 5) все «Снято с контроля» → Снято; 6) иначе → Зарегистрирован.
/// Пустой список → <see cref="DocumentAggregatedStatus.NotApplicable"/> (группа «Хранение» либо
/// «Исполнение» до первого назначения; в UI — прочерк). ОСОБЕННОСТЬ, унаследованная из СКИД намеренно:
/// смесь «Исполнено»+«Снято» даёт «Зарегистрирован» (ветка «иначе») — ТЗ эту смесь не определяет,
/// поведение сохранено как в исходнике.
/// </remarks>
public static class AggregatedStatusCalculator
{
    /// <summary>Вычисляет агрегированный статус по правилу §4.3 (см. remarks).</summary>
    public static DocumentAggregatedStatus Calculate(IReadOnlyList<AssignmentStatus> assignmentStatuses)
    {
        ArgumentNullException.ThrowIfNull(assignmentStatuses);

        if (assignmentStatuses.Count == 0)
        {
            return DocumentAggregatedStatus.NotApplicable;
        }

        if (assignmentStatuses.Any(s => s == AssignmentStatus.Overdue))
        {
            return DocumentAggregatedStatus.Overdue;
        }

        if (assignmentStatuses.Any(s => s == AssignmentStatus.PartiallyDone))
        {
            return DocumentAggregatedStatus.PartiallyDone;
        }

        if (assignmentStatuses.Any(s => s is AssignmentStatus.InProgress or AssignmentStatus.InControl))
        {
            return DocumentAggregatedStatus.InProgress;
        }

        if (assignmentStatuses.All(s => s == AssignmentStatus.Done))
        {
            return DocumentAggregatedStatus.Done;
        }

        if (assignmentStatuses.All(s => s == AssignmentStatus.Closed))
        {
            return DocumentAggregatedStatus.Closed;
        }

        return DocumentAggregatedStatus.Registered;
    }
}
