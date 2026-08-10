using FluentValidation;
using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.DocFlow.Domain.Enums;
using ISC.AI.Modules.DocFlow.Domain.Services;
using Mediator;
using System.Globalization;

namespace ISC.AI.Modules.DocFlow.Application.Features.Reports;

/// <summary>Справочные значения для формы отчётов: виды, форматы и статусы.</summary>
public static class ReportOptions
{
    /// <summary>Виды отчётов в порядке показа.</summary>
    public static IReadOnlyList<ReportKind> Kinds { get; } =
    [
        ReportKind.ByAssignee, ReportKind.ByInspector, ReportKind.ByDivision,
        ReportKind.ByDeadline, ReportKind.Overdue, ReportKind.ByDocumentType,
    ];

    /// <summary>Форматы выгрузки.</summary>
    public static IReadOnlyList<ReportFormat> Formats { get; } =
        [ReportFormat.Excel, ReportFormat.Word, ReportFormat.Pdf];

    /// <summary>Статусы назначения для фильтра.</summary>
    public static IReadOnlyList<AssignmentStatus> Statuses { get; } =
        [.. Enum.GetValues<AssignmentStatus>()];

    /// <summary>Подпись формата.</summary>
    public static string Label(ReportFormat format) => format switch
    {
        ReportFormat.Excel => "Excel (.xlsx)",
        ReportFormat.Word => "Word (.docx)",
        ReportFormat.Pdf => "PDF",
        _ => format.ToString(),
    };
}
