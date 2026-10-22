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
using ISC.AI.Modules.Media.Domain.Model;
using ISC.AI.Modules.Media.Domain.Services;
using ISC.AI.Profile.Investigation.Domain.Enums;
using ISC.AI.Profile.Investigation.Domain.Services;
using Mediator;

namespace ISC.AI.Profile.Investigation.Application.Features.Statistics;

/// <summary>Строка журнала суточной активности — колонки [АИС 6.3].</summary>
/// <param name="Number">№ п/п.</param>
/// <param name="OccurredAtUtc">Дата и время ввода, UTC.</param>
/// <param name="Group">Группа: ОН, ОУ, ОТМ или «—».</param>
/// <param name="Employee">Сотрудник, который ввёл материал.</param>
/// <param name="TaskNumber">№ задания (у прочих дел — номер дела).</param>
/// <param name="Objects">Объект учёта: псевдоним, а без него — ФИО.</param>
/// <param name="Material">Вид материала: фото, видео, аудио, документ, сводка, справка.</param>
/// <param name="Status">Статус: черновик, действующий, архивный (неактивный — сводка, снятая с учёта).</param>
public sealed record ActivityJournalRow(
    int Number, DateTime OccurredAtUtc, string Group, string Employee, string TaskNumber, string Objects, string Material, string Status);

/// <summary>Журнал суточной оперативной активности (ТФ-ОТЧ-05, [АИС 6.3], ADR-0061).</summary>
/// <param name="From">Начало периода.</param>
/// <param name="To">Конец периода (включительно).</param>
/// <param name="Direction">Отбор по группе; <see langword="null"/> — все в пределах допуска.</param>
/// <param name="Rows">Строки по времени.</param>
/// <param name="Truncated">Записей больше предела — показаны первые <see cref="ActivityJournalSource.MaxRows"/>.</param>
/// <param name="MaxClassification">Гриф журнала — наибольший гриф дел и материалов строк (ТБ-033).</param>
/// <param name="Directions">Группы для отбора — в пределах допуска.</param>
public sealed record ActivityJournal(
    DateOnly From,
    DateOnly To,
    CaseDirection? Direction,
    IReadOnlyList<ActivityJournalRow> Rows,
    bool Truncated,
    short MaxClassification,
    IReadOnlyList<CaseDirection> Directions);

/// <summary>
/// Сборка журнала суточной активности (ADR-0061 ч. 2): записи хранилища профиля дополняются видом носителя из каталога
/// «Медиа» и видимостью документа из документооборота — невидимое субъекту выпадает; статус сводки — по окну
/// редактирования; сотрудник — по справочнику; группа — отдел подразделения дела.
/// </summary>
/// <remarks>
/// ИНВАРИАНТЫ (ТБ-012/020/021/033). Право «Статистические отчёты»; контекст допуска fail-closed; носитель и документ
/// входят, только если их видит субъект по решётке их пакета; гриф — максимум по вошедшим строкам.
/// </remarks>
public sealed class ActivityJournalSource(
    IUserRoleStore roles,
    ISubjectProvider subjectProvider,
    IAccessContextProvider accessProvider,
    IActivityReportStore store,
    IMediaCatalog catalog,
    IDocumentLookup documents,
    IDivisionAdminStore divisions,
    IUserDirectory users,
    CaseReportOptions reportOptions,
    TimeProvider timeProvider)
{
    /// <summary>Предел числа строк журнала: больше — первые по времени и пометка.</summary>
    public const int MaxRows = 5000;

    // Порт документооборота принимает не больше 200 идентификаторов за вызов (как в справках, ADR-0053).
    private const int DocumentBatch = 200;
    private const int AssetBatch = 500;

    /// <summary>Журнал за период; <see langword="null"/> — права нет.</summary>
    public async Task<ActivityJournal?> ReadAsync(DateOnly from, DateOnly to, CaseDirection? direction, CancellationToken cancellationToken)
    {
        if (!await StatisticsReports.AllowedAsync(roles, subjectProvider, cancellationToken))
        {
            return null;
        }

        var access = await accessProvider.GetCurrentAsync(cancellationToken);
        var directions = DivisionDirections.Resolve(await divisions.ListAsync(cancellationToken));
        var (fromUtc, toUtc) = RecognitionReportBuilder.Period(from, to, timeProvider.LocalTimeZone);
        var data = await store.ListActivityAsync(
            fromUtc, toUtc, StatisticsReports.DivisionsOf(direction, directions), MaxRows, access, cancellationToken);

        var assets = new Dictionary<int, MediaAssetRow>();
        foreach (var batch in data.Entries.Where(e => e.MediaAssetId is not null).Select(e => e.MediaAssetId!.Value).Distinct().Chunk(AssetBatch))
        {
            foreach (var asset in await catalog.ListAsync(batch, access, cancellationToken))
            {
                assets[asset.Id] = asset;
            }
        }

        var docs = new Dictionary<int, DocumentBrief>();
        foreach (var batch in data.Entries.Where(e => e.DocumentId is not null).Select(e => e.DocumentId!.Value).Distinct().Chunk(DocumentBatch))
        {
            foreach (var (id, document) in await documents.ResolveByIdsAsync(batch, access, cancellationToken))
            {
                docs[id] = document;
            }
        }

        var names = (await users.ListActiveAsync(cancellationToken)).ToDictionary(u => u.Id, u => u.Name);
        foreach (var id in data.Entries.Where(e => e.UserId is not null).Select(e => e.UserId!.Value).Distinct().Where(id => !names.ContainsKey(id)).ToList())
        {
            names[id] = await users.GetNameAsync(id, cancellationToken) ?? "сотрудник № " + id.ToString(CultureInfo.InvariantCulture);
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var rows = new List<ActivityJournalRow>();
        short max = 0;
        foreach (var entry in data.Entries)
        {
            // Носитель и документ, не видимые субъекту по решётке их пакета, в журнал не попадают (ТБ-021).
            (string Material, string Status, short Classification)? described = entry.Material switch
            {
                ActivityMaterial.Media when entry.MediaAssetId is { } assetId && assets.TryGetValue(assetId, out var asset) =>
                    (MaterialLabel(asset.Kind), ActiveLabel, asset.Classification),
                ActivityMaterial.Document when entry.DocumentId is { } documentId && docs.TryGetValue(documentId, out var document) =>
                    ("документ", ActiveLabel, document.Classification),
                ActivityMaterial.Report when entry.ReportKind is { } kind =>
                    (kind == CaseReportKind.SummaryOn ? "сводка" : "справка",
                        StateLabel(reportOptions.StateAt(entry.OccurredAtUtc, entry.ReportIsActive, now)),
                        entry.ReportClassification ?? entry.CaseClassification),
                _ => null,
            };
            if (described is not { } d)
            {
                continue;
            }

            max = Math.Max(max, Math.Max(d.Classification, entry.CaseClassification));
            var objects = entry.PersonId is { } personId && data.Persons.TryGetValue(personId, out var person)
                ? person
                : data.CaseObjects.TryGetValue(entry.CaseId, out var caseObjects) ? caseObjects : "—";
            rows.Add(new ActivityJournalRow(
                rows.Count + 1,
                entry.OccurredAtUtc,
                directions.TryGetValue(entry.DivisionId, out var group) ? group.ShortLabel() : "—",
                entry.UserId is { } userId && names.TryGetValue(userId, out var name) ? name : "—",
                string.IsNullOrWhiteSpace(entry.TaskNumber) ? entry.CaseNumber : entry.TaskNumber,
                objects,
                d.Material,
                d.Status));
        }

        return new ActivityJournal(from, to, direction, rows, data.Truncated, max, StatisticsReports.Directions(access, directions));
    }

    /// <summary>Статус материала, который не правится по окну: носитель, документ.</summary>
    public const string ActiveLabel = "действующий";

    /// <summary>Вид носителя словами.</summary>
    public static string MaterialLabel(MediaKind kind) => kind switch
    {
        MediaKind.Image => "фото",
        MediaKind.Video => "видео",
        MediaKind.Audio => "аудио",
        _ => "носитель",
    };

    /// <summary>
    /// Состояние сводки → шкала Заказчика: в окне редактирования — «черновик», после окна — «архивный», снятая с учёта —
    /// «неактивный» (ADR-0061 ч. 2).
    /// </summary>
    public static string StateLabel(CaseReportState state) => state switch
    {
        CaseReportState.Editable => "черновик",
        CaseReportState.Archived => "архивный",
        CaseReportState.Inactive => "неактивный",
        _ => "—",
    };
}

/// <summary>Сборка таблицы журнала для экрана и выгрузки (колонки [АИС 6.3]).</summary>
public static class ActivityJournalBuilder
{
    /// <summary>Заголовок (он же основа имени файла).</summary>
    public const string Title = "Журнал суточной оперативной активности";

    /// <summary>Колонки — в порядке <see cref="Cells"/>.</summary>
    public static IReadOnlyList<string> Columns { get; } =
        ["№ п/п", "Дата и время", "Группа", "Сотрудник", "№ задания", "Объект (псевдоним)", "Вид материала", "Статус"];

    /// <summary>Ячейки строки.</summary>
    public static IReadOnlyList<string> Cells(ActivityJournalRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return
        [
            row.Number.ToString(CultureInfo.InvariantCulture),
            row.OccurredAtUtc.ToLocalTime().ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture),
            row.Group,
            row.Employee,
            row.TaskNumber,
            row.Objects,
            row.Material,
            row.Status,
        ];
    }

    /// <summary>Строки отбора: период, группа и пометка о пределе.</summary>
    public static IReadOnlyList<string> FilterLines(ActivityJournal journal)
    {
        ArgumentNullException.ThrowIfNull(journal);
        var lines = new List<string> { StatisticsReports.PeriodLine(journal.From, journal.To), StatisticsReports.GroupLine(journal.Direction) };
        if (journal.Truncated)
        {
            lines.Add($"Записей больше {ActivityJournalSource.MaxRows.ToString(CultureInfo.InvariantCulture)} — показаны первые по времени; сузьте период.");
        }

        return lines;
    }

    /// <summary>Представление для выгрузки: таблица и итоги по видам материалов.</summary>
    public static ReportView ToView(ActivityJournal journal, string generatedBy, DateTime generatedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(journal);
        var totals = journal.Rows
            .GroupBy(r => r.Material)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new ReportTotal(g.Key, g.Count().ToString(CultureInfo.InvariantCulture)))
            .Prepend(new ReportTotal("Всего записей", journal.Rows.Count.ToString(CultureInfo.InvariantCulture)))
            .ToList();
        return new ReportView(
            Title,
            ClassificationLevels.Marking(journal.MaxClassification),
            StatisticsReports.Stamp(generatedBy, generatedAtUtc),
            FilterLines(journal),
            Columns,
            [new ReportViewGroup("Материалы за период", journal.Rows.Select(r => new ReportViewRow(Cells(r))).ToList(), [])],
            totals);
    }
}

/// <summary>
/// Показать журнал суточной оперативной активности (ТФ-ОТЧ-05, [АИС 6.3], ADR-0061) за период и по группе. Построение —
/// запись <c>View</c> в журнале аудита (ТБ-030).
/// </summary>
/// <param name="From">Начало периода.</param>
/// <param name="To">Конец периода (включительно).</param>
/// <param name="Direction">Группа ОН/ОУ/ОТМ; <see langword="null"/> — все в пределах допуска.</param>
public sealed record ActivityJournalQuery(DateOnly From, DateOnly To, CaseDirection? Direction = null)
    : IRequest<ResponseDto<ActivityJournal>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.View;

    /// <inheritdoc />
    public string? AuditSummary => "investigation:reports:activity:view";

    /// <inheritdoc cref="ActivityJournalQuery" />
    public sealed class Handler(ActivityJournalSource source) : IRequestHandler<ActivityJournalQuery, ResponseDto<ActivityJournal>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<ActivityJournal>> Handle(ActivityJournalQuery query, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(query);
            return await source.ReadAsync(query.From, query.To, query.Direction, cancellationToken) is { } journal
                ? ResponseDto<ActivityJournal>.Ok(journal)
                : ResponseDto<ActivityJournal>.BadRequest(StatisticsReports.Denied);
        }
    }
}

/// <summary>
/// Выгрузить журнал суточной активности в Excel, Word или PDF (ОР-6.0.3): гриф — максимум строк, запись <c>Export</c>
/// до выдачи файла (ТБ-030/032/033).
/// </summary>
/// <param name="From">Начало периода.</param>
/// <param name="To">Конец периода (включительно).</param>
/// <param name="Direction">Группа.</param>
/// <param name="Format">Формат файла.</param>
public sealed record ExportActivityJournalCommand(DateOnly From, DateOnly To, CaseDirection? Direction, ReportFormat Format)
    : IRequest<ResponseDto<ReportDocument>>
{
    /// <inheritdoc cref="ExportActivityJournalCommand" />
    public sealed class Handler(
        ActivityJournalSource source,
        IEnumerable<IReportRenderer> renderers,
        ISubjectProvider subjectProvider,
        IUserDirectory users,
        IAuditWriter audit,
        TimeProvider timeProvider)
        : IRequestHandler<ExportActivityJournalCommand, ResponseDto<ReportDocument>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<ReportDocument>> Handle(ExportActivityJournalCommand command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);
            if (await source.ReadAsync(command.From, command.To, command.Direction, cancellationToken) is not { } journal)
            {
                return ResponseDto<ReportDocument>.BadRequest(StatisticsReports.Denied);
            }

            return await StatisticsReports.ExportAsync(
                renderers,
                command.Format,
                generatedBy => ActivityJournalBuilder.ToView(journal, generatedBy, timeProvider.GetUtcNow().UtcDateTime),
                journal.MaxClassification,
                "investigation:reports:activity; rows=" + journal.Rows.Count.ToString(CultureInfo.InvariantCulture),
                subjectProvider,
                users,
                audit,
                cancellationToken);
        }
    }
}

/// <summary>Правила журнала: «с» не позже «по», период не длиннее года (журнал — построчный), известная группа.</summary>
public sealed class ActivityJournalValidator : AbstractValidator<ActivityJournalQuery>
{
    /// <summary>Наибольшая длина периода журнала, дней.</summary>
    public const int MaxDays = 366;

    /// <inheritdoc cref="ActivityJournalValidator" />
    public ActivityJournalValidator()
    {
        RuleFor(q => q).Must(q => q.From <= q.To).WithMessage("Начало периода позже конца.");
        RuleFor(q => q).Must(q => q.To.DayNumber - q.From.DayNumber <= MaxDays).WithMessage("Период журнала не длиннее года.");
        RuleFor(q => q.Direction).IsInEnum().When(q => q.Direction is not null);
    }
}

/// <summary>Правила выгрузки журнала — те же, плюс известный формат.</summary>
public sealed class ExportActivityJournalValidator : AbstractValidator<ExportActivityJournalCommand>
{
    /// <inheritdoc cref="ExportActivityJournalValidator" />
    public ExportActivityJournalValidator()
    {
        RuleFor(c => c).Must(c => c.From <= c.To).WithMessage("Начало периода позже конца.");
        RuleFor(c => c).Must(c => c.To.DayNumber - c.From.DayNumber <= ActivityJournalValidator.MaxDays)
            .WithMessage("Период журнала не длиннее года.");
        RuleFor(c => c.Direction).IsInEnum().When(c => c.Direction is not null);
        RuleFor(c => c.Format).IsInEnum();
    }
}
