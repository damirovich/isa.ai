using System.Globalization;
using FluentValidation;
using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.DocFlow.Domain.Enums;
using ISC.AI.Modules.DocFlow.Domain.Services;
using Mediator;

namespace ISC.AI.Modules.DocFlow.Application.Reports;

/// <summary>
/// Сформировать отчёт (разд. 6 ТЗ СКИД) в выбранном формате.
/// </summary>
/// <remarks>
/// Это КОМАНДА, а не запрос, хотя данные она только читает: выгрузка отчёта — учитываемое событие
/// (аудит, ТБ-030), и называть её запросом значило бы приглашать обойти запись в журнал.
/// </remarks>
public sealed record GenerateReportCommand(ReportKind Kind, ReportFormat Format, ReportFilter Filter)
    : IRequest<ResponseDto<ReportDocument>>
{
    /// <inheritdoc cref="GenerateReportCommand" />
    public sealed class Handler(
        IReportDataSource dataSource,
        IEnumerable<IReportRenderer> renderers,
        IDivisionDirectory divisions,
        IUserDirectory users,
        IAccessContextProvider accessProvider,
        ISubjectProvider subjectProvider,
        IAuditWriter audit)
        : IRequestHandler<GenerateReportCommand, ResponseDto<ReportDocument>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<ReportDocument>> Handle(
            GenerateReportCommand command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);

            var renderer = renderers.FirstOrDefault(r => r.Format == command.Format);
            if (renderer is null)
            {
                return ResponseDto<ReportDocument>.Fail($"Формат отчёта не поддерживается: {command.Format}.");
            }

            // Fail-closed: без контекста допуска GetCurrentAsync бросает — отчёт не формируется вовсе
            // (ТБ-012). Разграничение применяет сам источник, В ЗАПРОСЕ.
            var access = await accessProvider.GetCurrentAsync(cancellationToken);
            var data = await dataSource.QueryAsync(command.Kind, command.Filter, access, cancellationToken);

            var subjectId = await subjectProvider.GetCurrentUserIdAsync(cancellationToken);
            var generatedBy = subjectId is { } id
                ? await users.GetNameAsync(id, cancellationToken) ?? UserLabel(id)
                : "—";

            var names = await NamesAsync(data, cancellationToken);
            var view = ReportViewBuilder.Build(
                command.Kind, command.Filter, data, names, generatedBy, DateTime.UtcNow);

            var document = renderer.Render(view);

            // Аудит ДО выдачи файла: если запись не удалась, пользователь не должен получить выгрузку
            // (ТБ-030 — фиксируются все обращения). Гриф записи — максимальный гриф выданных строк,
            // то есть не ниже грифа самих данных (ТБ-032).
            await audit.WriteAsync(
                new AuditEntry(
                    AuditAction.Export,
                    data.MaxClassification,
                    subjectId,
                    ObjectRef(command, data),
                    command.Filter.DivisionId,
                    // В чувствительной части — применённый отбор: по нему видно, ЧТО именно выгрузили.
                    // Самих строк здесь нет: журнал не должен становиться второй копией корпуса.
                    string.Join("; ", view.FilterLines)),
                cancellationToken);

            return ResponseDto<ReportDocument>.Ok(document);
        }

        private static string UserLabel(int id) =>
            $"пользователь №{id.ToString(CultureInfo.InvariantCulture)}";

        private static string ObjectRef(GenerateReportCommand command, ReportData data) =>
            $"report:{command.Kind}/{command.Format}; rows="
            + data.Rows.Count.ToString(CultureInfo.InvariantCulture);

        /// <summary>
        /// Подстановочные имена только для тех подразделений и людей, которые реально попали в отчёт.
        /// </summary>
        private async Task<ReportNames> NamesAsync(ReportData data, CancellationToken cancellationToken)
        {
            var divisionNames = (await divisions.ListAsync(cancellationToken))
                .ToDictionary(division => division.Id, division => division.Name);

            var userNames = (await users.ListActiveAsync(cancellationToken))
                .ToDictionary(user => user.Id, user => user.Name);

            // Исполнитель мог быть отключён уже после выдачи поручения — в списке активных его нет,
            // а в отчёте он должен остаться человеком, а не номером.
            var missing = data.Rows
                .SelectMany(row => new[] { row.AssigneeUserId, row.InspectorUserId })
                .OfType<int>()
                .Distinct()
                .Where(id => !userNames.ContainsKey(id));

            foreach (var id in missing)
            {
                if (await users.GetNameAsync(id, cancellationToken) is { } name)
                {
                    userNames[id] = name;
                }
            }

            return new ReportNames(divisionNames, userNames);
        }
    }
}

/// <summary>
/// Предпросмотр отчёта на экране — до формирования файла.
/// </summary>
/// <remarks>
/// Нужен ровно затем, чтобы не выгружать вслепую: отбор задаётся семью полями, и промахнуться легко,
/// а понять это по скачанному файлу можно только открыв его. Предпросмотр НЕ пишется в журнал как
/// выгрузка (<c>Export</c>): файла не возникло, ничего из системы не ушло. Число строк ограничено —
/// на экран всё равно не поместится больше.
/// </remarks>
public sealed record PreviewReportQuery(ReportKind Kind, ReportFilter Filter, int MaxRows = 200)
    : IRequest<ResponseDto<ReportView>>
{
    /// <inheritdoc cref="PreviewReportQuery" />
    public sealed class Handler(
        IReportDataSource dataSource,
        IDivisionDirectory divisions,
        IUserDirectory users,
        IAccessContextProvider accessProvider,
        ISubjectProvider subjectProvider)
        : IRequestHandler<PreviewReportQuery, ResponseDto<ReportView>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<ReportView>> Handle(
            PreviewReportQuery query, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(query);

            var access = await accessProvider.GetCurrentAsync(cancellationToken);
            var data = await dataSource.QueryAsync(query.Kind, query.Filter, access, cancellationToken);

            // Усечение ЧЕСТНОЕ: экран рядом пишет, сколько строк всего, чтобы «первые 200» не выдавали
            // себя за весь отчёт. Разграничение уже применено источником — здесь только показ.
            var limited = data.Rows.Count > query.MaxRows
                ? new ReportData([.. data.Rows.Take(query.MaxRows)], data.MaxClassification)
                : data;

            var subjectId = await subjectProvider.GetCurrentUserIdAsync(cancellationToken);
            var generatedBy = subjectId is { } id
                ? await users.GetNameAsync(id, cancellationToken) ?? "—"
                : "—";

            var names = new ReportNames(
                (await divisions.ListAsync(cancellationToken)).ToDictionary(d => d.Id, d => d.Name),
                (await users.ListActiveAsync(cancellationToken)).ToDictionary(u => u.Id, u => u.Name));

            var view = ReportViewBuilder.Build(
                query.Kind, query.Filter, limited, names, generatedBy, DateTime.UtcNow);

            return ResponseDto<ReportView>.Ok(view, data.Rows.Count);
        }
    }
}

/// <summary>Валидатор отбора отчёта: период обязателен и ограничен по длине.</summary>
public sealed class GenerateReportCommandValidator : AbstractValidator<GenerateReportCommand>
{
    /// <summary>
    /// Предельная длина периода. Отчёт грузится в память целиком и рендерится в файл; без потолка
    /// один запрос «с 1900 года» кладёт процесс, а пользы в такой выгрузке нет — она нечитаема.
    /// </summary>
    public const int MaxPeriodDays = 1100;

    /// <inheritdoc cref="GenerateReportCommandValidator" />
    public GenerateReportCommandValidator()
    {
        RuleFor(command => command.Filter).NotNull();

        RuleFor(command => command.Filter.To)
            .GreaterThanOrEqualTo(command => command.Filter.From)
            .WithMessage("Конец периода не может быть раньше начала.");

        RuleFor(command => command.Filter)
            .Must(filter => filter is null || filter.To.DayNumber - filter.From.DayNumber <= MaxPeriodDays)
            .WithMessage($"Период отчёта не может превышать {MaxPeriodDays} дней.");

        RuleFor(command => command.Kind).IsInEnum();
        RuleFor(command => command.Format).IsInEnum();
    }
}

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
