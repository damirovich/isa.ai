using System.Globalization;
using ISC.AI.Abstractions.BackgroundTasks;
using ISC.AI.Abstractions.Corpus;
using ISC.AI.Abstractions.Ingestion;
using ISC.AI.Profile.Investigation.Domain.Entities;
using ISC.AI.Profile.Investigation.Domain.Enums;
using ISC.AI.Profile.Investigation.Domain.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ISC.AI.Profile.Investigation.Data;

/// <summary>
/// Материалы дел профиля в корпусе ядра поверх <c>investigation.case_corpus_entry</c> (ADR-0051): «Сведения дела» и
/// действующие сводки/справки по бланку.
/// </summary>
/// <remarks>
/// <para>ОТМЕТКИ. Сведения дела: время изменения дела и число/последнее изменение фигурантов, адресов и транспорта (одни
/// агрегатные запросы на партию дел). Сводка/справка: номер актуальной редакции. Текст строится и кладётся в корпус только
/// там, где отметка изменилась; одинаковый текст корпус не дублирует (дедупликация по хешу).</para>
/// <para>ИНВАРИАНТЫ (ТБ-020/064/070). Дела в вызов передаёт вызывающий уже после решётки — здесь она не применяется
/// (работа и фоновая, без субъекта). Гриф и подразделение документа корпуса — дела. Прежняя версия текста и текст снятой
/// с действия сводки УНИЧТОЖАЮТСЯ (<see cref="IDocumentPurger"/>, акт в журнале), а не остаются «неактуальными».</para>
/// </remarks>
public sealed class CaseCorpusSync(
    IDbContextFactory<InvestigationDbContext> contextFactory,
    IIngestionPort ingestion,
    IDocumentPurger purger,
    TimeProvider time) : ICaseCorpusSync
{
    /// <summary>Дел в одной партии синхронизации.</summary>
    public const int Batch = 20;

    /// <inheritdoc />
    public async Task<IReadOnlyList<CaseCorpusRef>> SyncAsync(IReadOnlyCollection<int> caseIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caseIds);
        var ids = caseIds.Distinct().ToArray();
        foreach (var part in ids.Chunk(Batch))
        {
            await SyncPartAsync(part, cancellationToken);
        }

        return await ListAsync(ids, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CaseCorpusRef>> ListAsync(IReadOnlyCollection<int> caseIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caseIds);
        var ids = caseIds.Distinct().ToArray();
        if (ids.Length == 0)
        {
            return [];
        }

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var rows = await db.CaseCorpusEntries.AsNoTracking()
            .Where(e => ids.Contains(e.CaseId))
            .Select(e => new { e.Kind, e.SourceId, e.CaseId, e.CoreDocumentId, e.Case!.Number })
            .ToListAsync(cancellationToken);
        var reportIds = rows.Where(r => r.Kind == CaseCorpusKind.Report).Select(r => r.SourceId).ToArray();
        var reports = reportIds.Length == 0
            ? new Dictionary<int, (CaseReportKind Kind, DateOnly Date)>()
            : (await db.CaseReports.AsNoTracking()
                .Where(r => reportIds.Contains(r.Id))
                .Select(r => new { r.Id, r.Kind, r.ReportDate })
                .ToListAsync(cancellationToken))
                .ToDictionary(r => r.Id, r => (Kind: r.Kind, Date: r.ReportDate));

        return rows
            .Select(r => new CaseCorpusRef(
                r.Kind,
                r.SourceId,
                r.CaseId,
                r.CoreDocumentId,
                r.Kind == CaseCorpusKind.Report && reports.TryGetValue(r.SourceId, out var report)
                    ? CaseCorpusText.ReportLabel(report.Kind, report.Date, r.Number)
                    : CaseCorpusText.CardLabel(r.Number)))
            .ToList();
    }

    /// <inheritdoc />
    public async Task<int?> SyncNextAsync(int afterCaseId, CancellationToken cancellationToken = default)
    {
        int[] part;
        await using (var db = await contextFactory.CreateDbContextAsync(cancellationToken))
        {
            part = await db.Cases.AsNoTracking()
                .Where(c => c.Id > afterCaseId)
                .OrderBy(c => c.Id)
                .Select(c => c.Id)
                .Take(Batch)
                .ToArrayAsync(cancellationToken);
        }

        if (part.Length == 0)
        {
            return null;
        }

        await SyncPartAsync(part, cancellationToken);
        return part[^1];
    }

    /// <inheritdoc />
    public async Task<int> PurgeCaseAsync(int caseId, int? subjectId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var entries = await db.CaseCorpusEntries.Where(e => e.CaseId == caseId).ToListAsync(cancellationToken);
        foreach (var entry in entries)
        {
            // Каждый документ корпуса — своим актом в журнале ДО удаления (fail-closed внутри порта ядра).
            await purger.PurgeAsync(entry.CoreDocumentId, subjectId, cancellationToken);
        }

        db.CaseCorpusEntries.RemoveRange(entries);
        await db.SaveChangesAsync(cancellationToken);
        return entries.Count;
    }

    // Синхронизация партии видимых дел: сведения дела и действующие сводки/справки.
    private async Task SyncPartAsync(int[] part, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var heads = await db.Cases.AsNoTracking()
            .Where(c => part.Contains(c.Id))
            .Select(c => new
            {
                Head = new CaseCardHead(c.Id, c.Number, c.Title, c.Kind, c.Status, c.OpenedAt, c.TaskNumber, c.Basis, c.Purpose),
                c.Classification,
                c.DivisionId,
                Stamp = c.UpdatedAt ?? c.CreatedAt,
            })
            .ToListAsync(cancellationToken);
        if (heads.Count == 0)
        {
            return;
        }

        var persons = await db.Persons.AsNoTracking()
            .Where(p => part.Contains(p.CaseId))
            .GroupBy(p => p.CaseId)
            .Select(g => new { CaseId = g.Key, Count = g.Count(), Last = g.Max(p => p.UpdatedAt ?? p.CreatedAt) })
            .ToDictionaryAsync(g => g.CaseId, cancellationToken);
        var addresses = await db.PersonAddresses.AsNoTracking()
            .Where(a => part.Contains(a.Person!.CaseId))
            .GroupBy(a => a.Person!.CaseId)
            .Select(g => new { CaseId = g.Key, Count = g.Count(), Last = g.Max(a => a.UpdatedAt ?? a.CreatedAt) })
            .ToDictionaryAsync(g => g.CaseId, cancellationToken);
        var vehicles = await db.PersonVehicles.AsNoTracking()
            .Where(v => part.Contains(v.Person!.CaseId))
            .GroupBy(v => v.Person!.CaseId)
            .Select(g => new { CaseId = g.Key, Count = g.Count(), Last = g.Max(v => v.UpdatedAt ?? v.CreatedAt) })
            .ToDictionaryAsync(g => g.CaseId, cancellationToken);
        var phones = await db.PersonPhones.AsNoTracking()
            .Where(t => part.Contains(t.Person!.CaseId))
            .GroupBy(t => t.Person!.CaseId)
            .Select(g => new { CaseId = g.Key, Count = g.Count(), Last = g.Max(t => t.UpdatedAt ?? t.CreatedAt) })
            .ToDictionaryAsync(g => g.CaseId, cancellationToken);
        var reports = await db.CaseReports.AsNoTracking()
            .Where(r => part.Contains(r.CaseId) && r.IsActive)
            .Select(r => new { r.Id, r.CaseId, r.Kind, r.ReportDate, r.PersonId, r.CurrentRevision, r.Classification, r.DivisionId })
            .ToListAsync(cancellationToken);
        var entries = await db.CaseCorpusEntries.Where(e => part.Contains(e.CaseId)).ToListAsync(cancellationToken);

        foreach (var head in heads)
        {
            var p = persons.GetValueOrDefault(head.Head.CaseId);
            var a = addresses.GetValueOrDefault(head.Head.CaseId);
            var v = vehicles.GetValueOrDefault(head.Head.CaseId);
            var t = phones.GetValueOrDefault(head.Head.CaseId);

            // Номера телефонов (ADR-0055) входят в отметку только когда они есть: у дел без номеров отметка прежняя, и
            // появление номеров не пересобирает все карточки разом.
            var version = string.Create(
                CultureInfo.InvariantCulture,
                $"c{head.Stamp.Ticks}|p{p?.Count ?? 0}:{p?.Last.Ticks ?? 0}|a{a?.Count ?? 0}:{a?.Last.Ticks ?? 0}|v{v?.Count ?? 0}:{v?.Last.Ticks ?? 0}")
                + (t is null ? string.Empty : string.Create(CultureInfo.InvariantCulture, $"|t{t.Count}:{t.Last.Ticks}"));
            if (Find(entries, CaseCorpusKind.CaseCard, head.Head.CaseId)?.SourceVersion == version)
            {
                continue;
            }

            var text = CaseCorpusText.Card(head.Head, await LoadPersonsAsync(db, head.Head.CaseId, cancellationToken));
            await PutAsync(db, entries, CaseCorpusKind.CaseCard, head.Head.CaseId, head.Head.CaseId, version,
                new IngestionRequest(
                    CaseCorpusText.CardDocType,
                    CaseCorpusText.CardLabel(head.Head.Number),
                    text,
                    head.Classification,
                    head.DivisionId,
                    Source: CaseCorpusText.Source,
                    DocDate: head.Head.OpenedAt,
                    Metadata: Meta(head.Head.CaseId, reportId: null)),
                cancellationToken);
        }

        var numbers = heads.ToDictionary(h => h.Head.CaseId, h => h.Head.Number);
        foreach (var report in reports)
        {
            var version = "r" + report.CurrentRevision.ToString(CultureInfo.InvariantCulture);
            if (Find(entries, CaseCorpusKind.Report, report.Id)?.SourceVersion == version)
            {
                continue;
            }

            var content = CaseReportContent.FromJson(await db.CaseReportRevisions.AsNoTracking()
                .Where(r => r.ReportId == report.Id && r.Number == report.CurrentRevision)
                .Select(r => r.ContentJson)
                .FirstOrDefaultAsync(cancellationToken));
            var personName = report.PersonId is { } personId
                ? await db.Persons.AsNoTracking().Where(x => x.Id == personId).Select(x => x.DisplayName).FirstOrDefaultAsync(cancellationToken)
                : null;
            var number = numbers.GetValueOrDefault(report.CaseId, string.Empty);
            await PutAsync(db, entries, CaseCorpusKind.Report, report.Id, report.CaseId, version,
                new IngestionRequest(
                    report.Kind.Label(),
                    CaseCorpusText.ReportLabel(report.Kind, report.ReportDate, number),
                    CaseCorpusText.Report(report.Id, number, report.Kind, report.ReportDate, personName, report.CurrentRevision, content),
                    report.Classification,
                    report.DivisionId,
                    Source: CaseCorpusText.Source,
                    DocDate: report.ReportDate,
                    Metadata: Meta(report.CaseId, report.Id)),
                cancellationToken);
        }

        // Снятые с действия сводки и справки — из корпуса (уничтожением, не «неактуальностью»).
        var active = reports.Select(r => r.Id).ToHashSet();
        foreach (var stale in entries.Where(e => e.Kind == CaseCorpusKind.Report && !active.Contains(e.SourceId)).ToList())
        {
            db.CaseCorpusEntries.Remove(stale);
            await db.SaveChangesAsync(cancellationToken);
            await purger.PurgeAsync(stale.CoreDocumentId, subjectId: null, cancellationToken);
        }
    }

    private static async Task<IReadOnlyList<CaseCardPerson>> LoadPersonsAsync(InvestigationDbContext db, int caseId, CancellationToken cancellationToken)
    {
        var persons = await db.Persons.AsNoTracking()
            .Where(p => p.CaseId == caseId)
            .OrderBy(p => p.Role).ThenBy(p => p.Id)
            .Select(p => new
            {
                p.Id, p.DisplayName, p.Alias, p.Role, p.RoleInCase, p.Sex, p.BirthDate, p.BirthYear,
                p.BirthPlace, p.WorkPlace, p.Residence, p.Notes, p.Citizenship, p.Orientation,
            })
            .ToListAsync(cancellationToken);
        var ids = persons.Select(p => p.Id).ToArray();
        var addresses = (await db.PersonAddresses.AsNoTracking()
                .Where(a => ids.Contains(a.PersonId))
                .OrderBy(a => a.Id)
                .Select(a => new { a.PersonId, a.Kind, a.Text })
                .ToListAsync(cancellationToken))
            .ToLookup(a => a.PersonId);
        var vehicles = (await db.PersonVehicles.AsNoTracking()
                .Where(v => ids.Contains(v.PersonId))
                .OrderBy(v => v.Id)
                .Select(v => new { v.PersonId, v.Make, v.Model, v.Color, v.PlateNumber })
                .ToListAsync(cancellationToken))
            .ToLookup(v => v.PersonId);
        var phones = (await db.PersonPhones.AsNoTracking()
                .Where(t => ids.Contains(t.PersonId))
                .OrderBy(t => t.Kind).ThenBy(t => t.Id)
                .Select(t => new { t.PersonId, t.Kind, t.Number })
                .ToListAsync(cancellationToken))
            .ToLookup(t => t.PersonId);

        return persons
            .Select(p => new CaseCardPerson(
                p.DisplayName, p.Alias, p.Role, p.RoleInCase, p.Sex, p.BirthDate, p.BirthYear, p.BirthPlace, p.WorkPlace, p.Residence, p.Notes,
                addresses[p.Id].Select(a => (a.Kind, a.Text)).ToList(),
                vehicles[p.Id]
                    .Select(v => string.Join(' ', new[] { v.Make, v.Model, v.Color }.Where(s => !string.IsNullOrWhiteSpace(s)))
                        + (string.IsNullOrWhiteSpace(v.PlateNumber) ? string.Empty : ", госномер " + v.PlateNumber))
                    .Select(s => s.Trim(' ', ','))
                    .Where(s => s.Length > 0)
                    .ToList(),
                p.Citizenship,
                p.Orientation,
                phones[p.Id].Select(t => (t.Kind, t.Number)).ToList()))
            .ToList();
    }

    // Положить текст в корпус и обновить строку; прежняя версия — уничтожается после записи новой.
    private async Task PutAsync(
        InvestigationDbContext db,
        List<CaseCorpusEntry> entries,
        CaseCorpusKind kind,
        int sourceId,
        int caseId,
        string version,
        IngestionRequest request,
        CancellationToken cancellationToken)
    {
        var result = await ingestion.IngestAsync(request, cancellationToken);
        if (!result.Accepted || result.DocumentId is not { } coreDocumentId)
        {
            return; // отказ ядра (нет грифа/подразделения) — повтор при следующей синхронизации
        }

        var now = time.GetUtcNow().UtcDateTime;
        var entry = Find(entries, kind, sourceId);
        int? replaced = null;
        if (entry is null)
        {
            entry = new CaseCorpusEntry
            {
                CaseId = caseId, Kind = kind, SourceId = sourceId, CoreDocumentId = coreDocumentId, SourceVersion = version, SyncedAt = now,
            };
            db.CaseCorpusEntries.Add(entry);
            entries.Add(entry);
        }
        else
        {
            replaced = entry.CoreDocumentId == coreDocumentId ? null : entry.CoreDocumentId;
            entry.CoreDocumentId = coreDocumentId;
            entry.SourceVersion = version;
            entry.SyncedAt = now;
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Параллельная синхронизация того же дела успела раньше — её строка верна, наша отбрасывается.
            db.ChangeTracker.Clear();
            entries.Remove(entry);
            return;
        }

        if (replaced is { } old)
        {
            await purger.PurgeAsync(old, subjectId: null, cancellationToken);
        }
    }

    private static CaseCorpusEntry? Find(List<CaseCorpusEntry> entries, CaseCorpusKind kind, int sourceId) =>
        entries.FirstOrDefault(e => e.Kind == kind && e.SourceId == sourceId);

    private static Dictionary<string, string> Meta(int caseId, int? reportId)
    {
        var meta = new Dictionary<string, string> { [CaseCorpusText.CaseIdKey] = caseId.ToString(CultureInfo.InvariantCulture) };
        if (reportId is { } id)
        {
            meta[CaseCorpusText.ReportIdKey] = id.ToString(CultureInfo.InvariantCulture);
        }

        return meta;
    }
}

/// <summary>
/// Фоновое дозаполнение корпуса материалами дел после старта хоста (ADR-0051): партиями по <see cref="CaseCorpusSync.Batch"/>
/// дел в общей очереди — следующая партия встаёт в конец, и задачи следователей не ждут весь архив.
/// </summary>
public sealed partial class CaseCorpusStartup(IServiceScopeFactory scopeFactory, ILogger<CaseCorpusStartup> logger) : IHostedService
{
    /// <summary>Вид задачи фоновой очереди.</summary>
    public const string TaskKind = "investigation:case-corpus-backfill";

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            if (scope.ServiceProvider.GetService<IBackgroundTaskQueue>() is { } queue)
            {
                await queue.EnqueueAsync(TaskKind, (sp, ct) => RunBatchAsync(sp, 0, ct), cancellationToken);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogEnqueueFailed(logger, exception);
        }
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Одна партия; остались дела — следующая партия в конец очереди.</summary>
    public static async Task RunBatchAsync(IServiceProvider services, int afterCaseId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(services);
        var last = await services.GetRequiredService<ICaseCorpusSync>().SyncNextAsync(afterCaseId, cancellationToken);
        if (last is { } next && services.GetService<IBackgroundTaskQueue>() is { } queue)
        {
            await queue.EnqueueAsync(TaskKind, (sp, ct) => RunBatchAsync(sp, next, ct), cancellationToken);
        }
    }

    [LoggerMessage(Level = LogLevel.Error,
        Message = "Дозаполнение поиска ИИ материалами дел (корпус ядра, ADR-0051) при старте не поставлено — старт не прерван; материалы дел синхронизируются при вопросах в чате.")]
    private static partial void LogEnqueueFailed(ILogger logger, Exception exception);
}
