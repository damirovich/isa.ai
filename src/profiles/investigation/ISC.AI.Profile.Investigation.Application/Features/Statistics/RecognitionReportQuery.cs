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
using ISC.AI.Modules.Media.Domain.Services;
using ISC.AI.Profile.Investigation.Domain.Services;
using Mediator;

namespace ISC.AI.Profile.Investigation.Application.Features.Statistics;

/// <summary>
/// Общая часть показа и выгрузки отчёта результативности (ADR-0061): право, контекст допуска, счёт пакета «Медиа» под
/// решёткой и наименования подразделений.
/// </summary>
/// <remarks>
/// ИНВАРИАНТЫ. (1) Право — строка матрицы «Статистические отчёты» (ТБ-012), без него счёт не выполняется. (2) Контекст
/// допуска — fail-closed (ТБ-021): без действующего допуска исключение, а не отчёт без отбора. (3) Счёт и гриф — в
/// пакете под решёткой (<see cref="IMediaStatistics"/>), здесь только подписи.
/// </remarks>
public sealed class RecognitionReportSource(
    IUserRoleStore roles,
    ISubjectProvider subjectProvider,
    IAccessContextProvider accessProvider,
    IMediaStatistics statistics,
    IDivisionAdminStore divisions,
    TimeProvider timeProvider)
{
    /// <summary>Отказ без права «Статистические отчёты».</summary>
    public const string Denied = "Статистические отчёты закрыты для вашей роли. Обратитесь к Администратору.";

    /// <summary>Отчёт за период; <see langword="null"/> — права нет.</summary>
    public async Task<(RecognitionReport Report, IReadOnlyDictionary<int, string> Names)?> ReadAsync(
        DateOnly from, DateOnly to, int? divisionId, CancellationToken cancellationToken)
    {
        if (!await PermissionRule.CallerHasAsync(roles, subjectProvider, InvestigationPermissions.ReportsStatistics, cancellationToken))
        {
            return null;
        }

        var access = await accessProvider.GetCurrentAsync(cancellationToken);
        var (fromUtc, toUtc) = RecognitionReportBuilder.Period(from, to, timeProvider.LocalTimeZone);
        var counters = await statistics.RecognitionAsync(fromUtc, toUtc, divisionId, access, cancellationToken);
        var names = RecognitionReportBuilder.Names(await divisions.ListAsync(cancellationToken));
        return (RecognitionReportBuilder.Build(counters, names, access, from, to, divisionId), names);
    }
}

/// <summary>
/// Показать отчёт результативности распознавания (ТФ-ОТЧ-07, [АИС 6.5], ADR-0061) за период: по подразделениям в
/// пределах допуска, без ИИ. Построение — запись <c>View</c> в журнале (ТБ-030).
/// </summary>
/// <param name="From">Начало периода (включительно).</param>
/// <param name="To">Конец периода (включительно).</param>
/// <param name="DivisionId">Отбор по подразделению; <see langword="null"/> — все разрешённые.</param>
public sealed record RecognitionReportQuery(DateOnly From, DateOnly To, int? DivisionId = null)
    : IRequest<ResponseDto<RecognitionReport>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.View;

    /// <inheritdoc />
    public string? AuditSummary => "investigation:reports:recognition:view";

    /// <inheritdoc cref="RecognitionReportQuery" />
    public sealed class Handler(RecognitionReportSource source) : IRequestHandler<RecognitionReportQuery, ResponseDto<RecognitionReport>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<RecognitionReport>> Handle(RecognitionReportQuery query, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(query);
            return await source.ReadAsync(query.From, query.To, query.DivisionId, cancellationToken) is { } read
                ? ResponseDto<RecognitionReport>.Ok(read.Report)
                : ResponseDto<RecognitionReport>.BadRequest(RecognitionReportSource.Denied);
        }
    }
}

/// <summary>
/// Выгрузить отчёт результативности в Excel, Word или PDF (ОР-6.0.3) — той же таблицей, что на экране, с маркировкой
/// грифа и «Сформировал».
/// </summary>
/// <remarks>
/// Это КОМАНДА, как выгрузка отчётов документооборота: выгрузка — учитываемое событие. ИНВАРИАНТ (ТБ-030/032/033):
/// запись <c>Export</c> с грифом отчёта и отбором пишется ДО выдачи файла — не записалась, файла нет; гриф файла —
/// наибольший гриф записей, вошедших в счёт.
/// </remarks>
/// <param name="From">Начало периода (включительно).</param>
/// <param name="To">Конец периода (включительно).</param>
/// <param name="DivisionId">Отбор по подразделению.</param>
/// <param name="Format">Формат файла.</param>
public sealed record ExportRecognitionReportCommand(DateOnly From, DateOnly To, int? DivisionId, ReportFormat Format)
    : IRequest<ResponseDto<ReportDocument>>
{
    /// <inheritdoc cref="ExportRecognitionReportCommand" />
    public sealed class Handler(
        RecognitionReportSource source,
        IEnumerable<IReportRenderer> renderers,
        ISubjectProvider subjectProvider,
        IUserDirectory users,
        IAuditWriter audit,
        TimeProvider timeProvider)
        : IRequestHandler<ExportRecognitionReportCommand, ResponseDto<ReportDocument>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<ReportDocument>> Handle(ExportRecognitionReportCommand command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);

            if (renderers.FirstOrDefault(r => r.Format == command.Format) is not { } renderer)
            {
                return ResponseDto<ReportDocument>.Fail($"Формат отчёта не поддерживается: {command.Format}.");
            }

            if (await source.ReadAsync(command.From, command.To, command.DivisionId, cancellationToken) is not { } read)
            {
                return ResponseDto<ReportDocument>.BadRequest(RecognitionReportSource.Denied);
            }

            var subjectId = await subjectProvider.GetCurrentUserIdAsync(cancellationToken);
            var generatedBy = subjectId is { } id
                ? await users.GetNameAsync(id, cancellationToken) ?? "сотрудник № " + id.ToString(CultureInfo.InvariantCulture)
                : "—";
            var view = RecognitionReportBuilder.ToView(read.Report, read.Names, generatedBy, timeProvider.GetUtcNow().UtcDateTime);
            var document = renderer.Render(view);

            // Журнал ДО выдачи файла (ТБ-030): гриф записи — гриф отчёта (ТБ-032); в чувствительной части — отбор, без
            // самих чисел: журнал не копия отчёта.
            await audit.WriteAsync(
                new AuditEntry(
                    AuditAction.Export,
                    read.Report.MaxClassification,
                    subjectId,
                    $"investigation:reports:recognition:export:{command.Format}; rows="
                        + read.Report.Rows.Count.ToString(CultureInfo.InvariantCulture),
                    command.DivisionId,
                    string.Join("; ", view.FilterLines)),
                cancellationToken);

            return ResponseDto<ReportDocument>.Ok(document);
        }
    }
}

/// <summary>Правила отчёта результативности: период не пустой и не длиннее пяти лет, подразделение — номер.</summary>
public sealed class RecognitionReportValidator : AbstractValidator<RecognitionReportQuery>
{
    /// <summary>Наибольшая длина периода, дней.</summary>
    public const int MaxDays = 5 * 366;

    /// <inheritdoc cref="RecognitionReportValidator" />
    public RecognitionReportValidator()
    {
        RuleFor(q => q).Must(q => q.From <= q.To).WithMessage("Начало периода позже конца.");
        RuleFor(q => q).Must(q => q.To.DayNumber - q.From.DayNumber <= MaxDays).WithMessage("Период не длиннее пяти лет.");
        RuleFor(q => q.DivisionId).GreaterThan(0).When(q => q.DivisionId is not null);
    }
}

/// <summary>Правила выгрузки отчёта результативности — те же, что у показа, плюс известный формат.</summary>
public sealed class ExportRecognitionReportValidator : AbstractValidator<ExportRecognitionReportCommand>
{
    /// <inheritdoc cref="ExportRecognitionReportValidator" />
    public ExportRecognitionReportValidator()
    {
        RuleFor(c => c).Must(c => c.From <= c.To).WithMessage("Начало периода позже конца.");
        RuleFor(c => c).Must(c => c.To.DayNumber - c.From.DayNumber <= RecognitionReportValidator.MaxDays)
            .WithMessage("Период не длиннее пяти лет.");
        RuleFor(c => c.DivisionId).GreaterThan(0).When(c => c.DivisionId is not null);
        RuleFor(c => c.Format).IsInEnum();
    }
}
