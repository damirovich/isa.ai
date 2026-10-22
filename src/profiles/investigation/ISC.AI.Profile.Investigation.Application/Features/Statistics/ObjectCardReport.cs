using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentValidation;
using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.DocFlow.Domain.Services;
using ISC.AI.Profile.Investigation.Domain.Enums;
using ISC.AI.Profile.Investigation.Domain.Services;
using Mediator;

namespace ISC.AI.Profile.Investigation.Application.Features.Statistics;

/// <summary>Строка статистики карточек: инициатор и счёт карточек.</summary>
/// <param name="Initiator">Подразделение-инициатор (справочник «Инициаторы заданий») или «инициатор не указан».</param>
/// <param name="Count">Счёт.</param>
public sealed record ObjectCardReportRow(string Initiator, ObjectCardCount Count);

/// <summary>Статистика карточек объектов по подразделениям-инициаторам (ОР-6.0.1, ТФ-ОТЧ-04, ADR-0061).</summary>
/// <param name="From">Начало периода.</param>
/// <param name="To">Конец периода (включительно).</param>
/// <param name="Direction">Группа ОН/ОУ/ОТМ; <see langword="null"/> — все в пределах допуска.</param>
/// <param name="Rows">Строки по наименованию инициатора («не указан» — последней).</param>
/// <param name="Total">Итог.</param>
/// <param name="MaxClassification">Гриф — наибольший гриф учтённых карточек.</param>
/// <param name="Directions">Группы для отбора — в пределах допуска.</param>
public sealed record ObjectCardReport(
    DateOnly From,
    DateOnly To,
    CaseDirection? Direction,
    IReadOnlyList<ObjectCardReportRow> Rows,
    ObjectCardCount Total,
    short MaxClassification,
    IReadOnlyList<CaseDirection> Directions);

/// <summary>
/// Сборка статистики карточек (ADR-0061 ч. 3): счёт хранилища профиля (то же правило дел, что реестр) и наименования
/// инициаторов из справочника, включая выключенные — карточка живёт дольше записи справочника.
/// </summary>
public sealed class ObjectCardReportSource(
    IUserRoleStore roles,
    ISubjectProvider subjectProvider,
    IAccessContextProvider accessProvider,
    IActivityReportStore store,
    IReferenceStore references,
    IDivisionAdminStore divisions,
    TimeProvider timeProvider)
{
    /// <summary>Подпись строки без инициатора.</summary>
    public const string NoInitiator = "инициатор не указан";

    /// <summary>Статистика за период; <see langword="null"/> — права нет.</summary>
    public async Task<ObjectCardReport?> ReadAsync(DateOnly from, DateOnly to, CaseDirection? direction, CancellationToken cancellationToken)
    {
        if (!await StatisticsReports.AllowedAsync(roles, subjectProvider, cancellationToken))
        {
            return null;
        }

        var access = await accessProvider.GetCurrentAsync(cancellationToken);
        var directions = DivisionDirections.Resolve(await divisions.ListAsync(cancellationToken));
        var (fromUtc, toUtc) = RecognitionReportBuilder.Period(from, to, timeProvider.LocalTimeZone);
        var counts = await store.CountObjectCardsAsync(
            fromUtc, toUtc, StatisticsReports.DivisionsOf(direction, directions), access, cancellationToken);
        var initiators = (await references.ListAsync(ReferenceKind.InitiatorUnit, cancellationToken)).ToDictionary(r => r.Id, r => r.Name);

        var rows = counts.Rows
            .Select(c => new ObjectCardReportRow(
                c.InitiatorUnitId is { } id
                    ? initiators.TryGetValue(id, out var name) ? name : "инициатор № " + id.ToString(CultureInfo.InvariantCulture)
                    : NoInitiator,
                c))
            .OrderBy(r => r.Count.InitiatorUnitId is null)
            .ThenBy(r => r.Initiator, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        var total = rows.Aggregate(
            new ObjectCardCount(null, 0, 0, 0, 0),
            (sum, r) => new ObjectCardCount(
                null, sum.Total + r.Count.Total, sum.InProgress + r.Count.InProgress, sum.Suspended + r.Count.Suspended, sum.Closed + r.Count.Closed));
        return new ObjectCardReport(from, to, direction, rows, total, counts.MaxClassification, StatisticsReports.Directions(access, directions));
    }
}

/// <summary>Таблица статистики карточек для экрана и выгрузки.</summary>
public static class ObjectCardReportBuilder
{
    /// <summary>Заголовок (он же основа имени файла).</summary>
    public const string Title = "Статистика карточек объектов по подразделениям-инициаторам";

    /// <summary>Колонки — в порядке ячеек.</summary>
    public static IReadOnlyList<string> Columns { get; } =
        ["Подразделение-инициатор", "Заведено карточек", "В работе", "Приостановлено", "Закрыто"];

    /// <summary>Ячейки счёта (без первой колонки).</summary>
    public static IReadOnlyList<string> Cells(ObjectCardCount count)
    {
        ArgumentNullException.ThrowIfNull(count);
        return [Number(count.Total), Number(count.InProgress), Number(count.Suspended), Number(count.Closed)];
    }

    /// <summary>Представление для выгрузки: строки по инициаторам и итог.</summary>
    public static ReportView ToView(ObjectCardReport report, string generatedBy, DateTime generatedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(report);
        return new ReportView(
            Title,
            ClassificationLevels.Marking(report.MaxClassification),
            StatisticsReports.Stamp(generatedBy, generatedAtUtc),
            [StatisticsReports.PeriodLine(report.From, report.To), StatisticsReports.GroupLine(report.Direction)],
            Columns,
            [new ReportViewGroup("По подразделениям-инициаторам", report.Rows.Select(r => new ReportViewRow([r.Initiator, .. Cells(r.Count)])).ToList(), [])],
            [
                new ReportTotal("Заведено карточек объектов", Number(report.Total.Total)),
                new ReportTotal("В работе", Number(report.Total.InProgress)),
                new ReportTotal("Приостановлено", Number(report.Total.Suspended)),
                new ReportTotal("Закрыто", Number(report.Total.Closed)),
            ]);
    }

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// Показать статистику карточек объектов по подразделениям-инициаторам (ОР-6.0.1, ТФ-ОТЧ-04, ADR-0061). Построение —
/// запись <c>View</c> в журнале аудита (ТБ-030).
/// </summary>
/// <param name="From">Начало периода.</param>
/// <param name="To">Конец периода (включительно).</param>
/// <param name="Direction">Группа ОН/ОУ/ОТМ; <see langword="null"/> — все в пределах допуска.</param>
public sealed record ObjectCardReportQuery(DateOnly From, DateOnly To, CaseDirection? Direction = null)
    : IRequest<ResponseDto<ObjectCardReport>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.View;

    /// <inheritdoc />
    public string? AuditSummary => "investigation:reports:object-cards:view";

    /// <inheritdoc cref="ObjectCardReportQuery" />
    public sealed class Handler(ObjectCardReportSource source) : IRequestHandler<ObjectCardReportQuery, ResponseDto<ObjectCardReport>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<ObjectCardReport>> Handle(ObjectCardReportQuery query, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(query);
            return await source.ReadAsync(query.From, query.To, query.Direction, cancellationToken) is { } report
                ? ResponseDto<ObjectCardReport>.Ok(report)
                : ResponseDto<ObjectCardReport>.BadRequest(StatisticsReports.Denied);
        }
    }
}

/// <summary>Выгрузить статистику карточек (ОР-6.0.3): гриф — максимум учтённых карточек, запись <c>Export</c> до выдачи файла.</summary>
/// <param name="From">Начало периода.</param>
/// <param name="To">Конец периода (включительно).</param>
/// <param name="Direction">Группа.</param>
/// <param name="Format">Формат файла.</param>
public sealed record ExportObjectCardReportCommand(DateOnly From, DateOnly To, CaseDirection? Direction, ReportFormat Format)
    : IRequest<ResponseDto<ReportDocument>>
{
    /// <inheritdoc cref="ExportObjectCardReportCommand" />
    public sealed class Handler(
        ObjectCardReportSource source,
        IEnumerable<IReportRenderer> renderers,
        ISubjectProvider subjectProvider,
        IUserDirectory users,
        IAuditWriter audit,
        TimeProvider timeProvider)
        : IRequestHandler<ExportObjectCardReportCommand, ResponseDto<ReportDocument>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<ReportDocument>> Handle(ExportObjectCardReportCommand command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);
            if (await source.ReadAsync(command.From, command.To, command.Direction, cancellationToken) is not { } report)
            {
                return ResponseDto<ReportDocument>.BadRequest(StatisticsReports.Denied);
            }

            return await StatisticsReports.ExportAsync(
                renderers,
                command.Format,
                generatedBy => ObjectCardReportBuilder.ToView(report, generatedBy, timeProvider.GetUtcNow().UtcDateTime),
                report.MaxClassification,
                "investigation:reports:object-cards; rows=" + report.Rows.Count.ToString(CultureInfo.InvariantCulture),
                subjectProvider,
                users,
                audit,
                cancellationToken);
        }
    }
}

/// <summary>Правила статистики карточек: «с» не позже «по», период не длиннее пяти лет, известная группа.</summary>
public sealed class ObjectCardReportValidator : AbstractValidator<ObjectCardReportQuery>
{
    /// <inheritdoc cref="ObjectCardReportValidator" />
    public ObjectCardReportValidator()
    {
        RuleFor(q => q).Must(q => q.From <= q.To).WithMessage("Начало периода позже конца.");
        RuleFor(q => q).Must(q => q.To.DayNumber - q.From.DayNumber <= RecognitionReportValidator.MaxDays).WithMessage("Период не длиннее пяти лет.");
        RuleFor(q => q.Direction).IsInEnum().When(q => q.Direction is not null);
    }
}

/// <summary>Правила выгрузки статистики карточек — те же, плюс известный формат.</summary>
public sealed class ExportObjectCardReportValidator : AbstractValidator<ExportObjectCardReportCommand>
{
    /// <inheritdoc cref="ExportObjectCardReportValidator" />
    public ExportObjectCardReportValidator()
    {
        RuleFor(c => c).Must(c => c.From <= c.To).WithMessage("Начало периода позже конца.");
        RuleFor(c => c).Must(c => c.To.DayNumber - c.From.DayNumber <= RecognitionReportValidator.MaxDays).WithMessage("Период не длиннее пяти лет.");
        RuleFor(c => c.Direction).IsInEnum().When(c => c.Direction is not null);
        RuleFor(c => c.Format).IsInEnum();
    }
}
