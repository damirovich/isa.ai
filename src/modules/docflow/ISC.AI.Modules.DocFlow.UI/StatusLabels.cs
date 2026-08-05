using ISC.AI.Modules.DocFlow.Domain.Enums;
using MudBlazor;

namespace ISC.AI.Modules.DocFlow.UI;

/// <summary>Русские подписи и цвета перечислений документооборота для страниц модуля (ТЗ СКИД §4.2/§4.3).</summary>
public static class StatusLabels
{
    /// <summary>Подпись статуса назначения.</summary>
    public static string Label(this AssignmentStatus status) => status switch
    {
        AssignmentStatus.Registered => "Зарегистрировано",
        AssignmentStatus.InControl => "Контроль",
        AssignmentStatus.InProgress => "В работе",
        AssignmentStatus.PartiallyDone => "Частично исполнено",
        AssignmentStatus.Done => "Исполнено",
        AssignmentStatus.Overdue => "Просрочено",
        AssignmentStatus.Closed => "Снято с контроля",
        _ => status.ToString(),
    };

    /// <summary>Подпись агрегированного статуса документа (NotApplicable — прочерк, §4.3).</summary>
    public static string Label(this DocumentAggregatedStatus status) => status switch
    {
        DocumentAggregatedStatus.NotApplicable => "—",
        DocumentAggregatedStatus.Registered => "Зарегистрирован",
        DocumentAggregatedStatus.InProgress => "В работе",
        DocumentAggregatedStatus.PartiallyDone => "Частично исполнено",
        DocumentAggregatedStatus.Done => "Исполнено",
        DocumentAggregatedStatus.Overdue => "Просрочено",
        DocumentAggregatedStatus.Closed => "Снят с контроля",
        _ => status.ToString(),
    };

    /// <summary>Цвет чипа агрегированного статуса.</summary>
    public static Color ChipColor(this DocumentAggregatedStatus status) => status switch
    {
        DocumentAggregatedStatus.Overdue => Color.Error,
        DocumentAggregatedStatus.PartiallyDone => Color.Warning,
        DocumentAggregatedStatus.InProgress => Color.Info,
        DocumentAggregatedStatus.Done => Color.Success,
        DocumentAggregatedStatus.Closed => Color.Dark,
        _ => Color.Default,
    };

    /// <summary>Цвет чипа статуса назначения.</summary>
    public static Color ChipColor(this AssignmentStatus status) => status switch
    {
        AssignmentStatus.Overdue => Color.Error,
        AssignmentStatus.PartiallyDone => Color.Warning,
        AssignmentStatus.InProgress or AssignmentStatus.InControl => Color.Info,
        AssignmentStatus.Done => Color.Success,
        AssignmentStatus.Closed => Color.Dark,
        _ => Color.Default,
    };

    /// <summary>Подпись направленности (§3.2).</summary>
    public static string Label(this DocumentDirection direction) => direction switch
    {
        DocumentDirection.Incoming => "Входящий",
        DocumentDirection.Internal => "Внутренний",
        DocumentDirection.Outgoing => "Исходящий",
        _ => direction.ToString(),
    };

    /// <summary>Подпись приоритета (§3.2).</summary>
    public static string Label(this DocumentPriority priority) => priority switch
    {
        DocumentPriority.Low => "Низкий",
        DocumentPriority.Medium => "Средний",
        DocumentPriority.High => "Высокий",
        _ => priority.ToString(),
    };

    /// <summary>Подпись группы типа (§1.4).</summary>
    public static string Label(this DocumentGroup group) =>
        group == DocumentGroup.Execution ? "Исполнение" : "Хранение";
}
