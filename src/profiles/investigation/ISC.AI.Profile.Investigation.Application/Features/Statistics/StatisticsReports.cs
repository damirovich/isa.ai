using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.DocFlow.Domain.Services;
using ISC.AI.Profile.Investigation.Domain.Enums;
using ISC.AI.Profile.Investigation.Domain.Services;

namespace ISC.AI.Profile.Investigation.Application.Features.Statistics;

/// <summary>
/// Общее для отчётных форм профиля (ADR-0061): право «Статистические отчёты», группы ОН/ОУ/ОТМ в пределах допуска,
/// подпись «Сформировал» и выгрузка с записью в журнал до выдачи файла.
/// </summary>
public static class StatisticsReports
{
    /// <summary>Отказ без права «Статистические отчёты».</summary>
    public const string Denied = RecognitionReportSource.Denied;

    /// <summary>Есть ли у вызывающего право «Статистические отчёты» (ТБ-012).</summary>
    public static Task<bool> AllowedAsync(IUserRoleStore roles, ISubjectProvider subjectProvider, CancellationToken cancellationToken) =>
        PermissionRule.CallerHasAsync(roles, subjectProvider, InvestigationPermissions.ReportsStatistics, cancellationToken);

    /// <summary>Группы (отделы ОН/ОУ/ОТМ), к подразделениям которых у субъекта есть допуск, — для отбора.</summary>
    public static IReadOnlyList<CaseDirection> Directions(AccessContext access, IReadOnlyDictionary<int, CaseDirection> directions)
    {
        ArgumentNullException.ThrowIfNull(access);
        ArgumentNullException.ThrowIfNull(directions);
        return access.AllowedDivisions
            .Where(directions.ContainsKey)
            .Select(id => directions[id])
            .Distinct()
            .Order()
            .ToList();
    }

    /// <summary>Подразделения группы — для сужения дел; без группы — <see langword="null"/> (все в пределах допуска).</summary>
    public static IReadOnlyCollection<int>? DivisionsOf(CaseDirection? direction, IReadOnlyDictionary<int, CaseDirection> directions)
    {
        ArgumentNullException.ThrowIfNull(directions);
        return direction is { } chosen ? directions.Where(d => d.Value == chosen).Select(d => d.Key).ToList() : null;
    }

    /// <summary>Строка отбора «Группа: …».</summary>
    public static string GroupLine(CaseDirection? direction) =>
        "Группа: " + (direction is { } d ? $"{d.ShortLabel()} — {d.Label()}" : "все в пределах допуска");

    /// <summary>Строка отбора «Период: …».</summary>
    public static string PeriodLine(DateOnly from, DateOnly to) =>
        $"Период: {from.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture)} — {to.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture)}";

    /// <summary>
    /// Выгрузка отчёта (ОР-6.0.3): отрисовка в формат и запись <c>Export</c> с грифом отчёта и отбором ДО выдачи файла
    /// (ТБ-030/032) — не записалась, файла нет.
    /// </summary>
    /// <param name="renderers">Отрисовщики форматов.</param>
    /// <param name="format">Формат.</param>
    /// <param name="build">Представление по подписи «кто сформировал».</param>
    /// <param name="classification">Гриф отчёта.</param>
    /// <param name="objectRef">Ссылка записи журнала — вид отчёта и формат, без содержимого.</param>
    /// <param name="subjectProvider">Кто выгружает.</param>
    /// <param name="users">Имена сотрудников.</param>
    /// <param name="audit">Журнал.</param>
    /// <param name="cancellationToken">Отмена.</param>
    public static async Task<ResponseDto<ReportDocument>> ExportAsync(
        IEnumerable<IReportRenderer> renderers,
        ReportFormat format,
        Func<string, ReportView> build,
        short classification,
        string objectRef,
        ISubjectProvider subjectProvider,
        IUserDirectory users,
        IAuditWriter audit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(renderers);
        ArgumentNullException.ThrowIfNull(build);
        ArgumentNullException.ThrowIfNull(subjectProvider);
        ArgumentNullException.ThrowIfNull(users);
        ArgumentNullException.ThrowIfNull(audit);

        if (renderers.FirstOrDefault(r => r.Format == format) is not { } renderer)
        {
            return ResponseDto<ReportDocument>.Fail($"Формат отчёта не поддерживается: {format}.");
        }

        var subjectId = await subjectProvider.GetCurrentUserIdAsync(cancellationToken);
        var generatedBy = subjectId is { } id
            ? await users.GetNameAsync(id, cancellationToken) ?? "сотрудник № " + id.ToString(CultureInfo.InvariantCulture)
            : "—";
        var view = build(generatedBy);
        var document = renderer.Render(view);

        await audit.WriteAsync(
            new AuditEntry(AuditAction.Export, classification, subjectId, $"{objectRef}:export:{format}", null, string.Join("; ", view.FilterLines)),
            cancellationToken);
        return ResponseDto<ReportDocument>.Ok(document);
    }

    /// <summary>Штамп «Сформировал» — как у отчётов документооборота.</summary>
    public static string Stamp(string generatedBy, DateTime generatedAtUtc) =>
        $"Сформировал: {generatedBy}. Дата и время: {generatedAtUtc.ToLocalTime().ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture)}.";
}
