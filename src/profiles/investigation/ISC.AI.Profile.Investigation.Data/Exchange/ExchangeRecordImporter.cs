using System.Globalization;
using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Investigation.Domain.Entities;
using ISC.AI.Profile.Investigation.Domain.Enums;
using ISC.AI.Profile.Investigation.Domain.Exchange;
using ISC.AI.Profile.Investigation.Domain.Services;
using Microsoft.EntityFrameworkCore;

namespace ISC.AI.Profile.Investigation.Data.Exchange;

/// <summary>Приём отклонён до первой записи — причина для Администратора (ADR-0068 v0.2).</summary>
public sealed class ExchangeImportRefusedException(string message) : Exception(message);

/// <summary>Дело получателя, принятое из пакета (сейчас или прежним приёмом), — куда привязывать его носители.</summary>
/// <param name="LocalId">Номер дела у получателя.</param>
/// <param name="Number">Номер дела.</param>
/// <param name="Title">Наименование.</param>
/// <param name="Classification">Гриф дела.</param>
/// <param name="DivisionId">Подразделение дела у получателя.</param>
public sealed record ExchangeImportedCase(int LocalId, string Number, string Title, short Classification, int DivisionId);

/// <summary>Счётчики приёма (ADR-0068 v0.2): что перенесено, что уже было, что ждёт.</summary>
public sealed class ExchangeImportTally
{
    /// <summary>Заведено дел.</summary>
    public int CasesCreated { get; set; }

    /// <summary>Заведено объектов.</summary>
    public int PersonsCreated { get; set; }

    /// <summary>Заведено адресов, телефонов и транспорта.</summary>
    public int RequisitesCreated { get; set; }

    /// <summary>Заведено событий связи.</summary>
    public int EventsCreated { get; set; }

    /// <summary>Заведено участников событий связи.</summary>
    public int ParticipantsCreated { get; set; }

    /// <summary>Заведено сводок.</summary>
    public int ReportsCreated { get; set; }

    /// <summary>Дополнено записей справочника.</summary>
    public int ReferencesCreated { get; set; }

    /// <summary>Принято носителей.</summary>
    public int AssetsCreated { get; set; }

    /// <summary>Получено файлов.</summary>
    public int FilesReceived { get; set; }

    /// <summary>Объём полученных файлов, байт.</summary>
    public long BytesReceived { get; set; }

    /// <summary>Записей, уже принятых прежде.</summary>
    public int AlreadyImported { get; set; }

    /// <summary>Конфликтов.</summary>
    public int Conflicts { get; set; }

    /// <summary>Записей, не перенесённых вместе с конфликтным делом (объекты, сводки, носители).</summary>
    public int HeldWithConflict { get; set; }

    /// <summary>Отложено до этапа 3 (эталоны, появления).</summary>
    public int Deferred { get; set; }

    /// <summary>Носителей без файла в пакете и ещё не принятых.</summary>
    public int MissingFiles { get; set; }

    /// <summary>Файлов, отклонённых общим приёмом «Медиа».</summary>
    public int Rejected { get; set; }

    /// <summary>Номера дел-конфликтов и имена файлов носителей-конфликтов — в защищённую часть акта.</summary>
    public List<string> ConflictLabels { get; } = [];

    /// <summary>Причины отклонения файлов — в акт.</summary>
    public List<string> Rejections { get; } = [];
}

/// <summary>Итог переноса записей — карты, нужные приёму носителей.</summary>
/// <param name="Cases">Дела пакета, принятые сейчас или прежде: номер источника → дело получателя.</param>
/// <param name="Users">Сотрудники: номер источника → своя учётка (по имени входа).</param>
public sealed record ExchangeRecordResult(
    IReadOnlyDictionary<int, ExchangeImportedCase> Cases,
    IReadOnlyDictionary<int, int> Users);

/// <summary>
/// Перенос записей пакета в базу получателя (ТФ-ОБМ-02, ADR-0068 v0.2): подразделения — по коду, справочник — по виду и
/// названию (недостающее заводится), сотрудники — по имени входа; дела, объекты с реквизитами, сводки и события связи —
/// новые записи с грифом и подразделением источника; совпадения с делами получателя — конфликты (этап 3).
/// </summary>
/// <remarks>
/// <para>
/// ИНВАРИАНТ ИДЕМПОТЕНТНОСТИ (ЦС-08): каждая заведённая запись и её строка карты <see cref="ExchangeSourceRecord"/>
/// пишутся одной транзакцией (дело со всем содержимым — одной). Сбой посередине оставляет только целые, сопоставленные
/// записи; повторный приём того же пакета их пропускает и доводит остальное.
/// </para>
/// <para>
/// ИНВАРИАНТ ДОСТУПА (ТБ-079): записи пишутся мимо допуска Администратора, с грифом каждой записи источника — гриф не
/// понижается. Нормализованные поля (ФИО, адреса, телефоны, номера машин, текст сводки) считаются теми же правилами, что
/// при ручном вводе: принятое находится поиском и пересечениями так же.
/// </para>
/// </remarks>
public sealed class ExchangeRecordImporter(
    IDbContextFactory<InvestigationDbContext> contextFactory,
    IUserAccountStore accounts,
    TimeProvider timeProvider)
{
    /// <summary>Переносит записи пакета; носители — отдельно (<see cref="ExchangeImportService"/>).</summary>
    /// <exception cref="ExchangeImportRefusedException">У получателя нет подразделений источника — ничего не записано.</exception>
    public async Task<ExchangeRecordResult> ImportAsync(
        int importId, string source, ExchangePackageData data, ExchangeImportTally tally, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(tally);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        var map = (await db.ExchangeSourceRecords.AsNoTracking()
                .Where(r => r.SourceInstance == source)
                .Select(r => new { r.Kind, r.SourceId, r.LocalId })
                .ToListAsync(cancellationToken))
            .ToDictionary(r => (r.Kind, r.SourceId), r => r.LocalId);

        var divisions = await ResolveDivisionsAsync(db, data, cancellationToken);
        var references = await ResolveReferencesAsync(db, data, tally, cancellationToken);
        var users = await ResolveUsersAsync(data, cancellationToken);
        var run = new Run(importId, source, map, divisions, references, users, tally, timeProvider.GetUtcNow().UtcDateTime);

        var cases = new Dictionary<int, ExchangeImportedCase>();
        var persons = new Dictionary<int, int>();
        var personsByCase = data.Persons.ToLookup(p => p.CaseSourceId);
        var reportsByCase = data.Reports.ToLookup(r => r.CaseSourceId);
        foreach (var exchangeCase in data.Cases.OrderBy(c => c.SourceId))
        {
            var imported = await ImportCaseAsync(db, run, exchangeCase, personsByCase[exchangeCase.SourceId], reportsByCase[exchangeCase.SourceId],
                persons, cancellationToken);
            if (imported is not null)
            {
                cases[exchangeCase.SourceId] = imported;
            }
        }

        await ImportLinkEventsAsync(db, run, data.LinkEvents, persons, cancellationToken);

        // Эталоны и появления ждут этапа 3: им нужно лицо в индексации получателя, а она идёт после приёма носителей.
        tally.Deferred += data.ReferencePhotos.Count + data.Appearances.Count;
        return new ExchangeRecordResult(cases, users);
    }

    /// <summary>Своя запись, сопоставленная записи источника; <see langword="null"/> — ещё не принималась.</summary>
    public async Task<int?> FindMappedAsync(string source, ExchangeRecordKind kind, int sourceId, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.ExchangeSourceRecords.AsNoTracking()
            .Where(r => r.SourceInstance == source && r.Kind == kind && r.SourceId == sourceId)
            .Select(r => (int?)r.LocalId)
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>Свои записи вида <paramref name="kind"/>, принятые из источника прежде (по карте).</summary>
    public async Task<HashSet<int>> MappedLocalIdsAsync(string source, ExchangeRecordKind kind, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return (await db.ExchangeSourceRecords.AsNoTracking()
                .Where(r => r.SourceInstance == source && r.Kind == kind)
                .Select(r => r.LocalId)
                .ToListAsync(cancellationToken))
            .ToHashSet();
    }

    /// <summary>
    /// Снимает конфликт записи источника, которая теперь принята (совпадения больше нет): строка очереди остаётся с
    /// состоянием «снят», а не висит в очереди Руководителя.
    /// </summary>
    public async Task ClearConflictAsync(string source, ExchangeConflictKind kind, int sourceId, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await ClearConflictAsync(db, source, kind, sourceId, timeProvider.GetUtcNow().UtcDateTime, cancellationToken);
    }

    /// <summary>Записывает сопоставление (для носителя — сразу за приёмом файла).</summary>
    public async Task MapAsync(
        int importId, string source, ExchangeRecordKind kind, int sourceId, int localId, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        db.ExchangeSourceRecords.Add(NewMapping(importId, source, kind, sourceId, localId, timeProvider.GetUtcNow().UtcDateTime));
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Записывает конфликт для очереди Руководителя (этап 3); повтор приёма обновляет прежний, а не множит очередь.
    /// </summary>
    public async Task AddConflictAsync(
        int importId,
        string source,
        ExchangeConflictKind kind,
        int sourceId,
        int? localId,
        string reason,
        string? label,
        short classification,
        int divisionId,
        CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await UpsertConflictAsync(db, importId, source, kind, sourceId, localId, reason, label, classification, divisionId,
            timeProvider.GetUtcNow().UtcDateTime, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
    }

    // ---------- справочники ----------

    // Подразделение — ключ разграничения доступа: сопоставляется только по коду и только с заведённым у получателя.
    // Заводить его приём не вправе (ADR-0068 v0.2: опечатка в коде увела бы материалы мимо допусков) — отказ с перечнем.
    private static async Task<Dictionary<int, int>> ResolveDivisionsAsync(
        InvestigationDbContext db, ExchangePackageData data, CancellationToken cancellationToken)
    {
        var required = data.Cases.Select(c => c.DivisionSourceId)
            .Concat(data.Persons.Select(p => p.DivisionSourceId))
            .Concat(data.LinkEvents.Select(e => e.DivisionSourceId))
            .Concat(data.Reports.Select(r => r.DivisionSourceId))
            .Concat(data.Assets.Select(a => a.DivisionSourceId))
            .ToHashSet();
        var local = (await db.Divisions.AsNoTracking()
                .Where(d => d.Code != null)
                .Select(d => new { d.Id, d.Code })
                .ToListAsync(cancellationToken))
            .ToDictionary(d => CodeKey(d.Code!), d => d.Id, StringComparer.Ordinal);

        var result = new Dictionary<int, int>();
        var missing = new List<string>();
        var known = data.Divisions.ToDictionary(d => d.SourceId);
        foreach (var sourceId in required.Order())
        {
            if (!known.TryGetValue(sourceId, out var division))
            {
                throw new ExchangeImportRefusedException(
                    $"Содержимое пакета ссылается на подразделение источника № {sourceId.ToString(CultureInfo.InvariantCulture)}, которого в пакете нет — пакет повреждён.");
            }

            if (division.Code is { Length: > 0 } code && local.TryGetValue(CodeKey(code), out var localId))
            {
                result[sourceId] = localId;
            }
            else
            {
                missing.Add(division.Code is { Length: > 0 } ? $"«{division.Name}» (код {division.Code})" : $"«{division.Name}» (без кода)");
            }
        }

        if (missing.Count > 0)
        {
            throw new ExchangeImportRefusedException(
                "В справочнике подразделений этого офиса нет подразделений источника: " + string.Join(", ", missing)
                + ". Заведите их с теми же кодами («Администрирование → Подразделения») и примите пакет заново.");
        }

        return result;
    }

    private static string CodeKey(string code) => code.Trim().ToUpperInvariant();

    // Справочник — по виду и названию без учёта регистра; недостающее заводится (как при переносе из Access, ADR-0043).
    private static async Task<Dictionary<int, int>> ResolveReferencesAsync(
        InvestigationDbContext db, ExchangePackageData data, ExchangeImportTally tally, CancellationToken cancellationToken)
    {
        var local = await db.ReferenceItems.AsNoTracking()
            .Select(r => new { r.Id, r.Kind, r.Name, r.SortOrder })
            .ToListAsync(cancellationToken);
        var index = new Dictionary<(ReferenceKind, string), int>();
        foreach (var item in local)
        {
            index.TryAdd((item.Kind, NameKey(item.Name)), item.Id);
        }

        var nextOrder = local.GroupBy(r => r.Kind).ToDictionary(g => g.Key, g => g.Max(r => r.SortOrder) + 1);
        var result = new Dictionary<int, int>();
        foreach (var reference in data.References.OrderBy(r => r.SourceId))
        {
            var key = (reference.Kind, NameKey(reference.Name));
            if (!index.TryGetValue(key, out var id))
            {
                var created = new ReferenceItem
                {
                    Kind = reference.Kind,
                    Name = reference.Name.Trim(),
                    Code = string.IsNullOrWhiteSpace(reference.Code) ? null : reference.Code.Trim(),
                    SortOrder = nextOrder.GetValueOrDefault(reference.Kind, 1),
                    IsActive = true,
                };
                db.ReferenceItems.Add(created);
                await db.SaveChangesAsync(cancellationToken);
                nextOrder[reference.Kind] = created.SortOrder + 1;
                id = created.Id;
                index[key] = id;
                tally.ReferencesCreated++;
            }

            result[reference.SourceId] = id;
        }

        db.ChangeTracker.Clear();
        return result;
    }

    private static string NameKey(string name) => name.Trim().ToUpperInvariant();

    // Сотрудник — по имени входа: номера учёток у офисов разные. Нет учётки — ссылка пустая (сведения не теряются: имя
    // и ФИО в пакете, акт приёма называет пакет).
    private async Task<Dictionary<int, int>> ResolveUsersAsync(ExchangePackageData data, CancellationToken cancellationToken)
    {
        var local = (await accounts.ListAsync(cancellationToken))
            .GroupBy(a => a.UserName.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().UserId, StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<int, int>();
        foreach (var user in data.Users)
        {
            if (local.TryGetValue(user.UserName.Trim(), out var id))
            {
                result[user.SourceId] = id;
            }
        }

        return result;
    }

    // ---------- дело и его содержимое ----------

    private static async Task<ExchangeImportedCase?> ImportCaseAsync(
        InvestigationDbContext db,
        Run run,
        ExchangeCase exchangeCase,
        IEnumerable<ExchangePerson> casePersons,
        IEnumerable<ExchangeReport> caseReports,
        Dictionary<int, int> persons,
        CancellationToken cancellationToken)
    {
        var personList = casePersons.OrderBy(p => p.SourceId).ToList();
        var reportList = caseReports.OrderBy(r => r.SourceId).ToList();
        ExchangeImportedCase? imported = null;
        if (run.Map.TryGetValue((ExchangeRecordKind.Case, exchangeCase.SourceId), out var mappedId))
        {
            imported = await db.Cases.AsNoTracking()
                .Where(c => c.Id == mappedId)
                .Select(c => new ExchangeImportedCase(c.Id, c.Number, c.Title, c.Classification, c.DivisionId))
                .FirstOrDefaultAsync(cancellationToken);
            run.Tally.AlreadyImported++;
            if (imported is null)
            {
                // Принятое прежде дело у получателя уничтожено (ADR-0025) — заново его приём не заводит.
                run.Tally.HeldWithConflict += personList.Count + reportList.Count;
                return null;
            }
        }
        else
        {
            var divisionId = run.Divisions[exchangeCase.DivisionSourceId];
            var number = exchangeCase.Number.Trim();
            var conflict = await FindCaseConflictAsync(db, run, exchangeCase, divisionId, number, cancellationToken);
            if (conflict is { } clash)
            {
                await UpsertConflictAsync(db, run.ImportId, run.Source, ExchangeConflictKind.Case, exchangeCase.SourceId, clash.LocalId,
                    clash.Reason, number, exchangeCase.Classification, divisionId, run.Now, cancellationToken);
                await db.SaveChangesAsync(cancellationToken);
                db.ChangeTracker.Clear();
                run.Tally.Conflicts++;
                run.Tally.ConflictLabels.Add("дело " + number + " (" + clash.Reason + ")");
                run.Tally.HeldWithConflict += personList.Count + reportList.Count;
                return null;
            }
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (imported is null)
        {
            var entity = NewCase(run, exchangeCase);
            db.Cases.Add(entity);
            await db.SaveChangesAsync(cancellationToken);
            db.ExchangeSourceRecords.Add(NewMapping(run, ExchangeRecordKind.Case, exchangeCase.SourceId, entity.Id));
            await ClearConflictAsync(db, run.Source, ExchangeConflictKind.Case, exchangeCase.SourceId, run.Now, cancellationToken);
            imported = new ExchangeImportedCase(entity.Id, entity.Number, entity.Title, entity.Classification, entity.DivisionId);
            run.Tally.CasesCreated++;
        }

        // Объекты: сначала все (номера нужны связям внутри дела), затем связи «связь объекта» — в той же транзакции.
        var created = new List<(ExchangePerson Source, Person Entity)>();
        foreach (var person in personList)
        {
            if (run.Map.TryGetValue((ExchangeRecordKind.Person, person.SourceId), out var localPerson))
            {
                persons[person.SourceId] = localPerson;
                run.Tally.AlreadyImported++;
                continue;
            }

            var entity = await NewPersonAsync(db, run, imported.LocalId, person, cancellationToken);
            db.Persons.Add(entity);
            created.Add((person, entity));
        }

        await db.SaveChangesAsync(cancellationToken);
        foreach (var (person, entity) in created)
        {
            persons[person.SourceId] = entity.Id;
            db.ExchangeSourceRecords.Add(NewMapping(run, ExchangeRecordKind.Person, person.SourceId, entity.Id));
            run.Tally.PersonsCreated++;
        }

        foreach (var (person, entity) in created.Where(c => c.Source.LinkedToPersonSourceId is not null))
        {
            // Связь — только с объектом того же дела, уже принятым (ограничение таблицы держит роль «связь»).
            if (entity.Role == PersonRole.Link
                && persons.TryGetValue(person.LinkedToPersonSourceId!.Value, out var target)
                && target != entity.Id
                && (personList.Any(p => p.SourceId == person.LinkedToPersonSourceId) || await db.Persons.AnyAsync(p => p.Id == target && p.CaseId == imported.LocalId, cancellationToken)))
            {
                entity.LinkedToPersonId = target;
                entity.LinkTypeId = person.LinkTypeSourceId is { } type && run.References.TryGetValue(type, out var localType) ? localType : null;
            }
        }

        await db.SaveChangesAsync(cancellationToken);

        // Реквизиты объектов дела — и у новых, и у принятых прежде (новый телефон уже принятого объекта — новая запись).
        foreach (var person in personList)
        {
            if (persons.TryGetValue(person.SourceId, out var personId))
            {
                await AddRequisitesAsync(db, run, person, personId, cancellationToken);
            }
        }

        foreach (var report in reportList)
        {
            if (run.Map.TryGetValue((ExchangeRecordKind.Report, report.SourceId), out _))
            {
                run.Tally.AlreadyImported++;
                continue;
            }

            var entity = NewReport(run, imported.LocalId, report, persons);
            db.CaseReports.Add(entity.Report);
            db.CaseReportRevisions.Add(entity.Revision);
            await db.SaveChangesAsync(cancellationToken);
            db.ExchangeSourceRecords.Add(NewMapping(run, ExchangeRecordKind.Report, report.SourceId, entity.Report.Id));
            run.Tally.ReportsCreated++;
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        db.ChangeTracker.Clear();
        return imported;
    }

    // Конфликт дела (ТФ-ОБМ-02): тот же номер в том же подразделении (его и не завести — уникальный индекс) или та же
    // пара «ГУ + № задания» — решение по содержанию, его принимает Руководитель (этап 3), не приём.
    private static async Task<(int LocalId, string Reason)?> FindCaseConflictAsync(
        InvestigationDbContext db, Run run, ExchangeCase exchangeCase, int divisionId, string number, CancellationToken cancellationToken)
    {
        var sameNumber = await db.Cases.AsNoTracking()
            .Where(c => c.DivisionId == divisionId && c.Number == number)
            .Select(c => (int?)c.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (sameNumber is { } numberClash)
        {
            return (numberClash, "в подразделении уже есть дело с тем же номером");
        }

        if (exchangeCase.Task is { TaskNumber: { Length: > 0 } taskNumber, InitiatorUnitSourceId: { } unitSource }
            && run.References.TryGetValue(unitSource, out var unit))
        {
            // Дела, принятые из ЭТОГО ЖЕ источника, — не конфликт: у источника два дела с одной парой «ГУ + № задания»
            // существуют законно (например, перенос из Access завёл «7/» и «7/ (запись 3)»), а правило ТФ-ОБМ-02 ловит
            // совпадение со своими делами получателя (живая проверка 06.11.2026). Правило номера в подразделении так
            // не смягчается: его держит уникальный индекс таблицы.
            var fromSource = run.Map.Where(m => m.Key.Item1 == ExchangeRecordKind.Case).Select(m => m.Value).ToList();
            var trimmed = taskNumber.Trim();
            var sameTask = await db.Cases.AsNoTracking()
                .Where(c => c.InitiatorUnitId == unit && c.TaskNumber == trimmed && !fromSource.Contains(c.Id))
                .Select(c => (int?)c.Id)
                .FirstOrDefaultAsync(cancellationToken);
            if (sameTask is { } taskClash)
            {
                return (taskClash, "уже есть дело с той же парой «ГУ + № задания»");
            }
        }

        return null;
    }

    private static CaseFile NewCase(Run run, ExchangeCase source)
    {
        var task = source.Task;
        return new CaseFile
        {
            Number = source.Number.Trim(),
            Title = source.Title.Trim(),
            Kind = source.Kind,
            OpenedAt = source.OpenedAt,
            Status = source.Status,
            ClosedAt = source.ClosedAt,
            Basis = source.Basis,
            DivisionId = run.Divisions[source.DivisionSourceId],
            Classification = source.Classification,
            InvestigatorUserId = User(run, source.InvestigatorUserSourceId),
            CreatedByUserId = User(run, source.CreatedByUserSourceId),

            // Реквизиты задания — как в источнике (ограничение таблицы: у «задания по объекту» обязательны, у прочих пусты).
            TaskNumber = task?.TaskNumber,
            InitiatorUnitId = Reference(run, task?.InitiatorUnitSourceId),
            InitiatorName = task?.InitiatorName,
            InitiatorRankId = Reference(run, task?.InitiatorRankSourceId),
            InitiatorPositionId = Reference(run, task?.InitiatorPositionSourceId),
            InitiatorPhone = task?.InitiatorPhone,
            InitiatorDetails = task?.InitiatorDetails,
            Justification = task?.Justification,
            Purpose = task?.Purpose,
            TaskNotes = task?.TaskNotes,
        };
    }

    private static async Task<Person> NewPersonAsync(
        InvestigationDbContext db, Run run, int caseId, ExchangePerson source, CancellationToken cancellationToken)
    {
        int? unidentifiedNumber = null;
        if (source.IsUnidentified)
        {
            // Номер неустановленного лица — источника, если в деле получателя он свободен; занят — следующий.
            var taken = await db.Persons.AsNoTracking()
                .Where(p => p.CaseId == caseId && p.UnidentifiedNumber != null)
                .Select(p => p.UnidentifiedNumber!.Value)
                .ToListAsync(cancellationToken);
            var pending = db.ChangeTracker.Entries<Person>()
                .Where(e => e.Entity.CaseId == caseId && e.Entity.UnidentifiedNumber is not null)
                .Select(e => e.Entity.UnidentifiedNumber!.Value);
            var used = taken.Concat(pending).ToHashSet();
            unidentifiedNumber = source.UnidentifiedNumber is { } wanted && !used.Contains(wanted)
                ? wanted
                : used.Count == 0 ? 1 : used.Max() + 1;
        }

        var entity = new Person
        {
            CaseId = caseId,
            DisplayName = source.DisplayName.Trim(),
            IsUnidentified = source.IsUnidentified,
            UnidentifiedNumber = unidentifiedNumber,
            Role = source.Role,
            RoleInCase = source.RoleInCase,
            BirthDate = source.BirthDate,
            BirthYear = source.BirthDate?.Year ?? source.BirthYear,
            BirthPlace = source.BirthPlace,
            WorkPlace = source.WorkPlace,
            Residence = source.Residence,
            Sex = source.Sex,
            Alias = source.Alias,
            Citizenship = source.Citizenship,
            Orientation = source.Orientation,
            Notes = source.Notes,
            Classification = source.Classification,
            DivisionId = run.Divisions[source.DivisionSourceId],
        };

        // Нормализация — те же правила, что у ручного ввода (PersonStore): пересечения и поиск видят принятых так же.
        entity.NameNormalized = entity.IsUnidentified ? null : RequisiteNormalizer.PersonName(entity.DisplayName);
        entity.ResidenceNormalized = RequisiteNormalizer.Address(entity.Residence);
        return entity;
    }

    private static async Task AddRequisitesAsync(
        InvestigationDbContext db, Run run, ExchangePerson source, int personId, CancellationToken cancellationToken)
    {
        var divisionId = run.Divisions[source.DivisionSourceId];
        var mappings = new List<(ExchangeRecordKind Kind, int SourceId, object Entity)>();
        foreach (var address in source.Addresses.OrderBy(a => a.SourceId))
        {
            if (Skip(run, ExchangeRecordKind.Address, address.SourceId))
            {
                continue;
            }

            var entity = new PersonAddress
            {
                PersonId = personId,
                Kind = address.Kind,
                Text = address.Text.Trim(),
                TextNormalized = RequisiteNormalizer.Address(address.Text) ?? string.Empty,
                Notes = address.Notes,
                Classification = address.Classification,
                DivisionId = divisionId,
            };
            db.PersonAddresses.Add(entity);
            mappings.Add((ExchangeRecordKind.Address, address.SourceId, entity));
        }

        foreach (var phone in source.Phones.OrderBy(p => p.SourceId))
        {
            if (Skip(run, ExchangeRecordKind.Phone, phone.SourceId))
            {
                continue;
            }

            // Номер без цифр таблица не примет (ограничение ck_person_phone_normalized_digits); у источника его быть не
            // могло — такую строку пропускаем, а не роняем приём.
            if (RequisiteNormalizer.Phone(phone.Number) is not { } normalized)
            {
                continue;
            }

            var entity = new PersonPhone
            {
                PersonId = personId,
                Kind = phone.Kind,
                Number = phone.Number.Trim(),
                NumberNormalized = normalized,
                Notes = phone.Notes,
                Classification = phone.Classification,
                DivisionId = divisionId,
            };
            db.PersonPhones.Add(entity);
            mappings.Add((ExchangeRecordKind.Phone, phone.SourceId, entity));
        }

        foreach (var vehicle in source.Vehicles.OrderBy(v => v.SourceId))
        {
            if (Skip(run, ExchangeRecordKind.Vehicle, vehicle.SourceId))
            {
                continue;
            }

            var entity = new PersonVehicle
            {
                PersonId = personId,
                PlateNumber = vehicle.PlateNumber,
                PlateNormalized = RequisiteNormalizer.Plate(vehicle.PlateNumber),
                Make = vehicle.Make,
                Model = vehicle.Model,
                Color = vehicle.Color,
                Notes = vehicle.Notes,
                Classification = vehicle.Classification,
                DivisionId = divisionId,
            };
            db.PersonVehicles.Add(entity);
            mappings.Add((ExchangeRecordKind.Vehicle, vehicle.SourceId, entity));
        }

        if (mappings.Count == 0)
        {
            return;
        }

        await db.SaveChangesAsync(cancellationToken);
        foreach (var (kind, sourceId, entity) in mappings)
        {
            var localId = entity switch
            {
                PersonAddress a => a.Id,
                PersonPhone p => p.Id,
                PersonVehicle v => v.Id,
                _ => throw new InvalidOperationException("Неизвестный реквизит."),
            };
            db.ExchangeSourceRecords.Add(NewMapping(run, kind, sourceId, localId));
            run.Tally.RequisitesCreated++;
        }
    }

    private static (CaseReport Report, CaseReportRevision Revision) NewReport(
        Run run, int caseId, ExchangeReport source, Dictionary<int, int> persons)
    {
        // Содержимое — через ту же нормализацию, что при вводе сводки (CaseReportStore): поиск по тексту сводок одинаков.
        var content = CaseReportContent.FromJson(source.ContentJson).Normalize(source.Kind);
        var divisionId = run.Divisions[source.DivisionSourceId];
        var report = new CaseReport
        {
            CaseId = caseId,
            PersonId = source.PersonSourceId is { } person && persons.TryGetValue(person, out var localPerson) ? localPerson : null,
            Kind = source.Kind,
            ReportDate = source.ReportDate,
            IsActive = source.IsActive,
            CreatedByUserId = User(run, source.CreatedByUserSourceId),
            CurrentRevision = 1,
            SearchText = content.SearchText(source.Kind),
            Classification = source.Classification,
            DivisionId = divisionId,
        };
        var revision = new CaseReportRevision
        {
            Report = report,
            Number = 1,
            ContentJson = content.ToJson(),
            AuthorUserId = User(run, source.RevisionAuthorUserSourceId),
            Classification = source.Classification,
            DivisionId = divisionId,
        };
        return (report, revision);
    }

    // ---------- события связи ----------

    private static async Task ImportLinkEventsAsync(
        InvestigationDbContext db, Run run, IReadOnlyList<ExchangeLinkEvent> events, Dictionary<int, int> persons,
        CancellationToken cancellationToken)
    {
        foreach (var exchangeEvent in events.OrderBy(e => e.SourceId))
        {
            // Участники — только принятые объекты: объект конфликтного дела у получателя ещё не заведён.
            var participants = exchangeEvent.Participants.Where(p => persons.ContainsKey(p.PersonSourceId)).OrderBy(p => p.SourceId).ToList();
            var mapped = run.Map.TryGetValue((ExchangeRecordKind.LinkEvent, exchangeEvent.SourceId), out var localEventId);
            if (!mapped && participants.Count == 0)
            {
                run.Tally.HeldWithConflict++;
                continue;
            }

            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            if (mapped)
            {
                run.Tally.AlreadyImported++;
            }
            else
            {
                var entity = new LinkEvent
                {
                    Kind = exchangeEvent.Kind,
                    Title = exchangeEvent.Title.Trim(),
                    OccurredOn = exchangeEvent.OccurredOn,
                    Description = exchangeEvent.Description,
                    CreatedByUserId = User(run, exchangeEvent.CreatedByUserSourceId),
                    Classification = exchangeEvent.Classification,
                    DivisionId = run.Divisions[exchangeEvent.DivisionSourceId],
                };
                db.LinkEvents.Add(entity);
                await db.SaveChangesAsync(cancellationToken);
                db.ExchangeSourceRecords.Add(NewMapping(run, ExchangeRecordKind.LinkEvent, exchangeEvent.SourceId, entity.Id));
                localEventId = entity.Id;
                run.Tally.EventsCreated++;
            }

            var added = new List<(int SourceId, LinkEventParticipant Entity)>();
            foreach (var participant in participants)
            {
                if (Skip(run, ExchangeRecordKind.Participant, participant.SourceId))
                {
                    continue;
                }

                var personId = persons[participant.PersonSourceId];
                if (await db.LinkEventParticipants.AnyAsync(p => p.EventId == localEventId && p.PersonId == personId, cancellationToken))
                {
                    // Объект уже участник события у получателя (добавлен вручную) — второй строки таблица не примет.
                    continue;
                }

                var personDivision = await db.Persons.AsNoTracking().Where(p => p.Id == personId).Select(p => p.DivisionId)
                    .FirstAsync(cancellationToken);
                var entity = new LinkEventParticipant
                {
                    EventId = localEventId,
                    PersonId = personId,
                    Role = participant.Role,
                    Level = participant.Level,
                    InSearch = participant.InSearch,
                    VehiclePlate = participant.VehiclePlate,
                    VehiclePlateNormalized = RequisiteNormalizer.Plate(participant.VehiclePlate),
                    VehicleDetails = participant.VehicleDetails,
                    ContactAddress = participant.ContactAddress,
                    ContactAddressNormalized = RequisiteNormalizer.Address(participant.ContactAddress),
                    Classification = participant.Classification,
                    DivisionId = personDivision,
                };
                db.LinkEventParticipants.Add(entity);
                added.Add((participant.SourceId, entity));
            }

            await db.SaveChangesAsync(cancellationToken);
            foreach (var (sourceId, entity) in added)
            {
                db.ExchangeSourceRecords.Add(NewMapping(run, ExchangeRecordKind.Participant, sourceId, entity.Id));
                run.Tally.ParticipantsCreated++;
            }

            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            db.ChangeTracker.Clear();
        }
    }

    // ---------- общее ----------

    private static bool Skip(Run run, ExchangeRecordKind kind, int sourceId)
    {
        if (!run.Map.ContainsKey((kind, sourceId)))
        {
            return false;
        }

        run.Tally.AlreadyImported++;
        return true;
    }

    private static int? User(Run run, int? sourceId) =>
        sourceId is { } id && run.Users.TryGetValue(id, out var local) ? local : null;

    private static int? Reference(Run run, int? sourceId) =>
        sourceId is { } id && run.References.TryGetValue(id, out var local) ? local : null;

    private static ExchangeSourceRecord NewMapping(Run run, ExchangeRecordKind kind, int sourceId, int localId)
    {
        run.Map[(kind, sourceId)] = localId;
        return NewMapping(run.ImportId, run.Source, kind, sourceId, localId, run.Now);
    }

    private static ExchangeSourceRecord NewMapping(
        int importId, string source, ExchangeRecordKind kind, int sourceId, int localId, DateTime now) => new()
    {
        SourceInstance = source,
        Kind = kind,
        SourceId = sourceId,
        LocalId = localId,
        ImportId = importId,
        CreatedAt = now,
    };

    private static async Task UpsertConflictAsync(
        InvestigationDbContext db,
        int importId,
        string source,
        ExchangeConflictKind kind,
        int sourceId,
        int? localId,
        string reason,
        string? label,
        short classification,
        int divisionId,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var conflict = await db.ExchangeImportConflicts
            .FirstOrDefaultAsync(c => c.SourceInstance == source && c.Kind == kind && c.SourceId == sourceId, cancellationToken);
        if (conflict is null)
        {
            conflict = new ExchangeImportConflict
            {
                SourceInstance = source,
                Kind = kind,
                SourceId = sourceId,
                Reason = reason,
                Status = ExchangeConflictStatus.Pending,
                CreatedAt = now,
            };
            db.ExchangeImportConflicts.Add(conflict);
        }

        conflict.ImportId = importId;
        conflict.Status = ExchangeConflictStatus.Pending;
        conflict.LocalId = localId;
        conflict.Reason = reason.Length <= 500 ? reason : reason[..500];
        conflict.Label = label is { Length: > 500 } ? label[..500] : label;
        conflict.Classification = classification;
        conflict.DivisionId = divisionId;
        conflict.UpdatedAt = now;
    }

    private static Task<int> ClearConflictAsync(
        InvestigationDbContext db, string source, ExchangeConflictKind kind, int sourceId, DateTime now, CancellationToken cancellationToken) =>
        db.ExchangeImportConflicts
            .Where(c => c.SourceInstance == source && c.Kind == kind && c.SourceId == sourceId && c.Status == ExchangeConflictStatus.Pending)
            .ExecuteUpdateAsync(s => s
                .SetProperty(c => c.Status, ExchangeConflictStatus.Cleared)
                .SetProperty(c => c.UpdatedAt, now), cancellationToken);

    private sealed record Run(
        int ImportId,
        string Source,
        Dictionary<(ExchangeRecordKind, int), int> Map,
        IReadOnlyDictionary<int, int> Divisions,
        IReadOnlyDictionary<int, int> References,
        IReadOnlyDictionary<int, int> Users,
        ExchangeImportTally Tally,
        DateTime Now);
}
