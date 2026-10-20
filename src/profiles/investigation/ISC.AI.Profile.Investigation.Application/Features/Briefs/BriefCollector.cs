using System.Globalization;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.DocFlow.Domain.Services;
using ISC.AI.Modules.Media.Application;
using ISC.AI.Modules.Media.Domain.Model;
using ISC.AI.Modules.Media.Domain.Services;
using ISC.AI.Profile.Investigation.Domain.Enums;
using ISC.AI.Profile.Investigation.Domain.Services;

namespace ISC.AI.Profile.Investigation.Application.Features.Briefs;

/// <summary>Пределы справки (ADR-0053): таблица и обработка не бесконечны, а обрезанное называется числом.</summary>
public static class BriefLimits
{
    /// <summary>Строк в таблице справки.</summary>
    public const int MaxRows = 200;

    /// <summary>Поисков, по которым считаются исходы кандидатов.</summary>
    public const int MaxSessions = 100;

    /// <summary>Сводок и справок по бланку, содержимое которых входит в справку.</summary>
    public const int MaxReports = 60;

    /// <summary>Длина ячейки с произвольным текстом (содержание сводки, обоснование решения).</summary>
    public const int MaxCellLength = 2_000;

    /// <summary>
    /// Идентификаторов документов за один вызов порта поиска документов — его предел (<c>DocumentLookup.MaxBatch</c>):
    /// сверх него порт молча отбрасывает идентификаторы.
    /// </summary>
    public const int DocumentBatch = 200;
}

/// <summary>
/// Сбор фактов справки по делу и по фигуранту (ТФ-ОТЧ-01/02, ADR-0053): всё считает код, модель здесь не участвует.
/// </summary>
/// <remarks>
/// <para>ИНВАРИАНТЫ ДОСТУПА (ТБ-020/021/071). Факты берутся из тех же хранилищ, что карточки дела и фигуранта, и под
/// той же решёткой: дело и фигурант — по полной решётке профиля (недоступное — <see langword="null"/>, неотличимо от
/// несуществующего, ДО чтения остального); носители, документы, сессии поиска, адреса, транспорт, появления — под
/// floor'ом своих строк. Счётчики хранилищ без floor на строках (<c>AppearanceCount</c>, <c>MediaCount</c>,
/// <c>CandidateCount</c>) не используются: числа считаются по спискам, прошедшим решётку. Перечень дел области поиска
/// не раскрывается — только вид и число (ADR-0044); о чужих делах пересечений — только то, что разрешает ТБ-084.</para>
/// <para>ГРИФ (ТБ-033): гриф справки — максимум грифов ВСЕХ вошедших сведений (дело, фигуранты, появления, адреса,
/// транспорт, носители, документы, сессии и кандидаты, сводки, чужие дела пересечений).</para>
/// </remarks>
public sealed class BriefCollector(
    ICaseStore cases,
    IPersonStore persons,
    IPersonRequisiteStore requisites,
    IIntersectionStore intersections,
    ICaseDocumentStore caseDocuments,
    IDocumentLookup documents,
    ICaseReportStore reports,
    ISearchSessionStore sessions,
    IMediaCatalog catalog,
    IUserAccountStore accounts,
    IReferenceStore references,
    IDivisionAdminStore divisions,
    TimeProvider time,
    MediaSearchOptions? searchOptions = null)
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    // Шкала процентов схожести пакета «Медиа» (ADR-0054): справка показывает тот же процент, что экраны. Контейнер
    // всегда даёт настройки поиска; без них (сборка вручную) — шкала по умолчанию.
    private readonly SimilarityScale _scale = searchOptions?.Scale ?? SimilarityScale.Default;

    /// <summary>
    /// Справка по делу за период (ТФ-ОТЧ-02); <see langword="null"/> — дело не найдено или недоступно. Период по
    /// умолчанию — со дня открытия дела по сегодня; он сужает поиски по лицу и сводки, остальное — по делу целиком.
    /// </summary>
    public async Task<Brief?> CaseAsync(int caseId, DateOnly? from, DateOnly? to, AccessContext access, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(access);
        if (await cases.GetAsync(caseId, access, cancellationToken) is not { } details)
        {
            return null;
        }

        var grade = new Grade(details.Classification);
        var names = await NamesAsync(cancellationToken);
        // По умолчанию — со дня открытия по сегодня; дата открытия в будущем (опечатка, импорт) не даёт перевёрнутого
        // периода, явное начало позже сегодня — период из одного этого дня и дальше.
        var today = Today();
        var periodFrom = from ?? (details.OpenedAt < today ? details.OpenedAt : today);
        var periodTo = to ?? (periodFrom > today ? periodFrom : today);
        var personNames = new Dictionary<int, string>();
        var sections = new List<BriefSection>
        {
            await CaseSectionAsync(details, names, cancellationToken),
            await PersonsSectionAsync(caseId, access, grade, personNames, cancellationToken),
            await MediaSectionAsync(details, access, grade, cancellationToken),
            await DocumentsSectionAsync(caseId, access, grade, cancellationToken),
            AuthorizationsSection(details),
            await SearchesSectionAsync(
                "Поиски по лицу за период",
                (await sessions.ListByCaseAsync(caseId, access, cancellationToken))
                    .Where(s => InPeriod(LocalDate(s.CreatedAt), periodFrom, periodTo))
                    .ToList(),
                names, access, grade, cancellationToken),
            await ReportsSectionAsync(caseId, periodFrom, periodTo, access, grade, personNames, cancellationToken),
        };

        return new Brief(
            BriefKind.Case,
            caseId,
            caseId,
            "Справка по делу № " + details.Number,
            (string.IsNullOrWhiteSpace(details.Title) ? string.Empty : "«" + details.Title.Trim() + "» · ")
                + "период " + Date(periodFrom) + " — " + Date(periodTo),
            periodFrom,
            periodTo,
            time.GetUtcNow().UtcDateTime,
            grade.Value,
            details.DivisionId,
            sections);
    }

    /// <summary>Справка по фигуранту (ТФ-ОТЧ-01); <see langword="null"/> — фигурант не найден или недоступен.</summary>
    public async Task<Brief?> PersonAsync(int personId, AccessContext access, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(access);
        if (await persons.GetAsync(personId, access, cancellationToken) is not { } person
            || await cases.GetAsync(person.CaseId, access, cancellationToken) is not { } details)
        {
            return null;
        }

        var grade = new Grade(details.Classification);
        grade.Add(person.Classification);
        var names = await NamesAsync(cancellationToken);
        var appearances = await persons.ListAppearancesAsync(personId, access, cancellationToken);
        var references = await persons.ListReferencePhotosAsync(personId, access, cancellationToken);
        // Все эталоны, включая заменённые: поиски по прежнему эталону — тоже история фигуранта (их решения есть в
        // «Истории верификаций»).
        var ownFaces = references.Where(r => r.MediaFaceId is not null)
            .Select(r => r.MediaFaceId!.Value)
            .ToHashSet();

        var sections = new List<BriefSection>
        {
            PersonSection(person),
        };
        sections.AddRange(await RequisiteSectionsAsync(personId, access, grade, cancellationToken));
        sections.Add(CaseOfPersonSection(details, names));
        var (appearanceSection, candidates) = await AppearancesSectionAsync(appearances, names, access, grade, cancellationToken);
        sections.Add(appearanceSection);
        sections.Add(VerificationSection(appearances, candidates, names));
        sections.Add(await SearchesSectionAsync(
            "Поиски по эталонам фигуранта",
            (await sessions.ListByCaseAsync(person.CaseId, access, cancellationToken))
                .Where(s => s.SuggestedPersonRef == personId
                    || (s.ProbeFaceId is { } probe && ownFaces.Contains(probe))
                    || (s.ProbeFaceIds?.Any(ownFaces.Contains) ?? false))
                .ToList(),
            names, access, grade, cancellationToken));
        sections.Add(await LinksSectionAsync(person, access, grade, cancellationToken));
        sections.Add(await IntersectionsSectionAsync(personId, access, grade, cancellationToken));

        return new Brief(
            BriefKind.Person,
            personId,
            person.CaseId,
            "Справка по фигуранту: " + PersonName(person),
            "Дело № " + details.Number + (string.IsNullOrWhiteSpace(details.Title) ? string.Empty : " «" + details.Title.Trim() + "»"),
            null,
            null,
            time.GetUtcNow().UtcDateTime,
            grade.Value,
            details.DivisionId,
            sections);
    }

    // ---------------- справка по делу ----------------

    private async Task<BriefSection> CaseSectionAsync(CaseDetails details, IReadOnlyDictionary<int, string> names, CancellationToken cancellationToken)
    {
        var division = (await divisions.ListAsync(cancellationToken)).FirstOrDefault(d => d.Id == details.DivisionId)?.Name;
        var facts = new List<BriefFact>
        {
            new("Номер", details.Number),
            new("Название", details.Title),
            new("Вид", details.Kind.Label()),
        };
        Add(facts, "Отдел", details.Direction?.Label());
        facts.Add(new BriefFact("Статус", details.Status.Label()));
        facts.Add(new BriefFact("Открыто", Date(details.OpenedAt)));
        Add(facts, "Закрыто", details.ClosedAt is { } closed ? Date(LocalDate(closed)) : null);
        Add(facts, "Ведущий", Name(names, details.InvestigatorUserId));
        Add(facts, "Подразделение", division);
        Add(facts, "Основание", details.Basis);
        if (details.TaskRequisites is { } task)
        {
            Add(facts, "Номер задания", task.TaskNumber);
            Add(facts, "Цель задания", task.Purpose);
            Add(facts, "Обоснование задания", task.Justification);
        }

        facts.Add(new BriefFact("Гриф дела", ClassificationLevels.Label(details.Classification)));
        return new BriefSection("Дело", facts);
    }

    // personNames заполняется именами всех видимых фигурантов дела — таблица сводок называет объект так же.
    private async Task<BriefSection> PersonsSectionAsync(
        int caseId, AccessContext access, Grade grade, Dictionary<int, string> personNames, CancellationToken cancellationToken)
    {
        var list = await persons.ListByCaseAsync(caseId, access, cancellationToken);
        foreach (var person in list)
        {
            personNames[person.Id] = PersonName(person);
        }

        var rows = new List<IReadOnlyList<string>>();
        foreach (var person in list.Take(BriefLimits.MaxRows))
        {
            grade.Add(person.Classification);

            // Счётчики — по спискам под решёткой, а не по AppearanceCount/ReferencePhotoCount строки (там нет floor).
            var appearances = (await persons.ListAppearancesAsync(person.Id, access, cancellationToken)).Where(a => !a.IsRevoked).ToList();
            appearances.ForEach(a => grade.Add(a.Classification));
            var photos = (await persons.ListReferencePhotosAsync(person.Id, access, cancellationToken)).Count(r => r.SupersededById is null);
            rows.Add(
            [
                PersonName(person),
                person.Role.Label(),
                person.RoleInCase ?? string.Empty,
                Count(appearances.Count),
                Count(photos),
            ]);
        }

        return new BriefSection(
            "Фигуранты",
            [new BriefFact("Фигурантов", Count(list.Count))],
            new BriefTable(["Фигурант", "Роль", "Роль в деле", "Подтверждённых появлений", "Эталонов"], rows, list.Count),
            "Подтверждённое появление — следственная версия, требует процессуальной проверки.");
    }

    private async Task<BriefSection> MediaSectionAsync(CaseDetails details, AccessContext access, Grade grade, CancellationToken cancellationToken)
    {
        // Привязки дела — без floor на носителях: в справку только носители, видимые под решёткой каталога.
        var assets = await catalog.ListAsync(details.Media.Select(m => m.MediaAssetId).Distinct().ToList(), access, cancellationToken);
        foreach (var asset in assets)
        {
            grade.Add(asset.Classification);
        }

        var facts = new List<BriefFact>
        {
            new("Носителей", Count(assets.Count)),
            new("Видео", Count(assets.Count(a => a.Kind == MediaKind.Video))),
            new("Аудио", Count(assets.Count(a => a.Kind == MediaKind.Audio))),
            new("Фото", Count(assets.Count(a => a.Kind == MediaKind.Image && !a.IsEditedCopy))),
        };
        if (assets.Any(a => a.IsEditedCopy))
        {
            facts.Add(new BriefFact("Обработанных копий фото", Count(assets.Count(a => a.IsEditedCopy))));
        }

        facts.Add(new BriefFact("Найдено лиц на носителях", Count(assets.Sum(a => a.FaceCount))));
        facts.Add(new BriefFact("Расшифровано записей", Count(assets.Count(a => a.TranscriptStatus == TranscriptStatus.Done))));

        var places = details.Media.Where(m => !string.IsNullOrWhiteSpace(m.Place))
            .GroupBy(m => m.MediaAssetId)
            .ToDictionary(g => g.Key, g => g.First().Place!);
        var ordered = assets.OrderBy(a => a.CapturedAt ?? a.RecordedAt ?? new DateTimeOffset(a.CreatedAt, TimeSpan.Zero)).ToList();
        var rows = ordered.Take(BriefLimits.MaxRows)
            .Select(a => (IReadOnlyList<string>)
            [
                a.OriginalFileName,
                MediaKindLabel(a),
                a.CapturedAt is { } captured ? Date(DateOnly.FromDateTime(captured.LocalDateTime))
                    : a.RecordedAt is { } recorded ? "≈ " + Date(DateOnly.FromDateTime(recorded.LocalDateTime)) + " (по файлу)" : string.Empty,
                places.GetValueOrDefault(a.Id) ?? string.Empty,
                Count(a.FaceCount),
                a.TranscriptStatus == TranscriptStatus.Done ? "есть" : string.Empty,
            ])
            .ToList();
        return new BriefSection(
            "Материалы дела: носители",
            facts,
            new BriefTable(["Носитель", "Вид", "Дата съёмки", "Место", "Лиц", "Расшифровка"], rows, assets.Count));
    }

    private async Task<BriefSection> DocumentsSectionAsync(int caseId, AccessContext access, Grade grade, CancellationToken cancellationToken)
    {
        var links = await caseDocuments.ListAsync(caseId, access, cancellationToken) ?? [];

        // Порт разрешает не больше BriefLimits.DocumentBatch идентификаторов за вызов, лишние молча отбрасывает —
        // поэтому пачками: иначе документы сверх пачки пропали бы из справки без следа.
        var found = new Dictionary<int, DocumentBrief>();
        foreach (var batch in links.Select(l => l.DocumentId).Distinct().Chunk(BriefLimits.DocumentBatch))
        {
            foreach (var (id, document) in await documents.ResolveByIdsAsync(batch, access, cancellationToken))
            {
                found[id] = document;
            }
        }

        var visible = found.Values.OrderBy(d => d.RegDate).ThenBy(d => d.Id).ToList();
        foreach (var document in visible)
        {
            grade.Add(document.Classification);
        }

        var rows = visible.Take(BriefLimits.MaxRows)
            .Select(d => (IReadOnlyList<string>)[d.RegNumber ?? "без номера", Date(d.RegDate), d.TypeName, Clip(d.ShortContent)])
            .ToList();
        return new BriefSection(
            "Документы дела",
            [new BriefFact("Документов", Count(visible.Count))],
            new BriefTable(["Номер", "Дата", "Вид", "Содержание"], rows, visible.Count));
    }

    private static BriefSection AuthorizationsSection(CaseDetails details)
    {
        var rows = details.Authorizations
            .OrderBy(a => a.IssuedAt)
            .Take(BriefLimits.MaxRows)
            .Select(a => (IReadOnlyList<string>)
            [
                a.Kind.Label(),
                a.Reference,
                Date(a.IssuedAt),
                a.ValidUntil is { } until ? Date(until) : string.Empty,
            ])
            .ToList();
        return new BriefSection(
            "Основания поиска",
            [new BriefFact("Оснований", Count(details.Authorizations.Count))],
            new BriefTable(["Вид", "Реквизиты", "Дата", "Действует до"], rows, details.Authorizations.Count));
    }

    private async Task<BriefSection> ReportsSectionAsync(
        int caseId,
        DateOnly from,
        DateOnly to,
        AccessContext access,
        Grade grade,
        Dictionary<int, string> personNames,
        CancellationToken cancellationToken)
    {
        var inPeriod = (await reports.ListByCaseAsync(caseId, null, access, cancellationToken) ?? [])
            .Where(r => r.State != CaseReportState.Inactive && InPeriod(r.ReportDate, from, to))
            .OrderBy(r => r.ReportDate)
            .ThenBy(r => r.Id)
            .ToList();
        var rows = new List<IReadOnlyList<string>>();
        foreach (var report in inPeriod.Take(BriefLimits.MaxReports))
        {
            if (await reports.GetAsync(report.Id, access, cancellationToken) is not { } details)
            {
                continue;
            }

            grade.Add(details.Classification);
            // Объект — тем же именем, что в таблице фигурантов («неустановленное лицо N», без «№»).
            var person = report.PersonId is { } personId && personNames.TryGetValue(personId, out var known)
                ? known
                : report.PersonName ?? string.Empty;
            rows.Add([Date(report.ReportDate), report.Kind.Label(), person, Clip(ReportText(details.Content))]);
        }

        return new BriefSection(
            "Сводки и справки по бланку за период",
            [
                new BriefFact("Сводок ОН", Count(inPeriod.Count(r => r.Kind == CaseReportKind.SummaryOn))),
                new BriefFact("Справок ОУ", Count(inPeriod.Count(r => r.Kind == CaseReportKind.ReferenceUn))),
            ],
            new BriefTable(["Дата", "Вид", "Объект", "Содержание"], rows, inPeriod.Count),
            inPeriod.Count > BriefLimits.MaxReports
                ? string.Create(Ru, $"Содержание — по первым {BriefLimits.MaxReports} документам периода.")
                : null);
    }

    // ---------------- справка по фигуранту ----------------

    private static BriefSection PersonSection(PersonRow person)
    {
        var q = person.Questionnaire ?? PersonQuestionnaire.Empty;
        var facts = new List<BriefFact> { new("Фигурант", PersonName(person)) };
        Add(facts, "Псевдоним", q.Alias);
        facts.Add(new BriefFact("Роль", person.Role.Label()));
        Add(facts, "Роль в деле", person.RoleInCase);
        Add(facts, "Пол", q.Sex?.Label());
        Add(facts, "Дата рождения", q.BirthDate is { } birth ? Date(birth) : q.BirthYear?.ToString(CultureInfo.InvariantCulture));
        Add(facts, "Место рождения", q.BirthPlace);
        Add(facts, "Гражданство", q.Citizenship);
        Add(facts, "Место работы", q.WorkPlace);
        Add(facts, "Место жительства", q.Residence);
        Add(facts, "Ориентировка", q.Orientation);
        Add(facts, "Примечания", person.Notes);
        return new BriefSection("Фигурант", facts);
    }

    private async Task<IReadOnlyList<BriefSection>> RequisiteSectionsAsync(
        int personId, AccessContext access, Grade grade, CancellationToken cancellationToken)
    {
        var found = await requisites.GetAsync(personId, access, cancellationToken) ?? new PersonRequisites([], [], []);
        foreach (var address in found.Addresses)
        {
            grade.Add(address.Classification);
        }

        foreach (var vehicle in found.Vehicles)
        {
            grade.Add(vehicle.Classification);
        }

        // Номера телефонов (ADR-0055) — их гриф тоже поднимает гриф справки (ТБ-033).
        foreach (var phone in found.Phones)
        {
            grade.Add(phone.Classification);
        }

        return
        [
            new BriefSection(
                "Адреса",
                [],
                new BriefTable(
                    ["Вид", "Адрес", "Примечание"],
                    found.Addresses.Take(BriefLimits.MaxRows).Select(a => (IReadOnlyList<string>)[a.Kind.Label(), a.Text, a.Notes ?? string.Empty]).ToList(),
                    found.Addresses.Count)),
            new BriefSection(
                "Транспорт",
                [],
                new BriefTable(
                    ["Госномер", "Марка", "Модель", "Цвет", "Примечание"],
                    found.Vehicles.Take(BriefLimits.MaxRows)
                        .Select(v => (IReadOnlyList<string>)[v.PlateNumber ?? string.Empty, v.Make ?? string.Empty, v.Model ?? string.Empty, v.Color ?? string.Empty, v.Notes ?? string.Empty])
                        .ToList(),
                    found.Vehicles.Count)),
            new BriefSection(
                "Телефоны",
                [],
                new BriefTable(
                    ["Вид", "Номер", "Примечание"],
                    found.Phones.Take(BriefLimits.MaxRows).Select(p => (IReadOnlyList<string>)[p.Kind.Label(), p.Number, p.Notes ?? string.Empty]).ToList(),
                    found.Phones.Count)),
        ];
    }

    private static BriefSection CaseOfPersonSection(CaseDetails details, IReadOnlyDictionary<int, string> names)
    {
        var facts = new List<BriefFact>
        {
            new("Номер", details.Number),
            new("Название", details.Title),
            new("Вид", details.Kind.Label()),
            new("Статус", details.Status.Label()),
            new("Открыто", Date(details.OpenedAt)),
        };
        Add(facts, "Ведущий", Name(names, details.InvestigatorUserId));
        return new BriefSection("Дело фигуранта", facts);
    }

    // Появления с носителем, моментом и отрезком в записи; кандидаты верификации — для следующего раздела.
    private async Task<(BriefSection Section, IReadOnlyDictionary<int, SearchCandidateRow> Candidates)> AppearancesSectionAsync(
        IReadOnlyList<AppearanceRow> appearances, IReadOnlyDictionary<int, string> names, AccessContext access, Grade grade, CancellationToken cancellationToken)
    {
        foreach (var appearance in appearances)
        {
            grade.Add(appearance.Classification);
        }

        var shown = appearances.Take(BriefLimits.MaxRows).ToList();
        var assets = (await catalog.ListAsync(shown.Select(a => a.MediaAssetId).Distinct().ToList(), access, cancellationToken))
            .ToDictionary(a => a.Id);
        foreach (var asset in assets.Values)
        {
            grade.Add(asset.Classification);
        }

        var spans = await catalog.GetTrackSpansAsync(
            shown.Where(a => assets.ContainsKey(a.MediaAssetId))
                .Select(a => new FaceTrackRequest(a.MediaFaceId, a.MediaAssetId, a.FrameTimestampMs))
                .ToList(),
            access,
            cancellationToken);

        var candidates = new Dictionary<int, SearchCandidateRow>();
        var rows = new List<IReadOnlyList<string>>();
        foreach (var appearance in shown)
        {
            if (await sessions.GetCandidateAsync(appearance.CandidateId, access, cancellationToken) is { } candidate)
            {
                candidates[appearance.Id] = candidate;
                grade.Add(candidate.Classification);
            }

            assets.TryGetValue(appearance.MediaAssetId, out var asset);
            spans.TryGetValue(appearance.MediaFaceId, out var span);
            rows.Add(
            [
                Date(LocalDate(appearance.ConfirmedAtUtc)),
                asset is null ? "носитель недоступен" : asset.OriginalFileName + " (" + MediaKindLabel(asset) + ")",
                Moment(appearance.FrameTimestampMs, span),
                RecordedMoment(asset, appearance.FrameTimestampMs),
                _scale.DisplayPercent(appearance.Similarity).ToString(Ru) + " %",
                Name(names, appearance.ExpertUserId) ?? string.Empty,
                // Режим одного оператора (ADR-0054): вторая подпись отсутствует, а не неизвестна.
                appearance.VerifierUserId is null ? "— (один оператор)" : Name(names, appearance.VerifierUserId) ?? string.Empty,
                appearance.IsRevoked
                    ? "отозвано" + (appearance.RevokedAtUtc is { } revoked ? " " + Date(LocalDate(revoked)) : string.Empty)
                    : TwoPersonRule.ConfirmedMarker,
            ]);
        }

        var active = appearances.Count(a => !a.IsRevoked);
        return (
            new BriefSection(
                "Подтверждённые появления",
                [
                    new BriefFact("Подтверждённых появлений", Count(active)),
                    new BriefFact("Отозванных", Count(appearances.Count - active)),
                ],
                new BriefTable(
                    ["Подтверждено", "Носитель", "Момент в записи", "Время записи по файлу", "Схожесть (вероятностная оценка)", "Эксперт", "Верификатор", "Статус"],
                    rows,
                    appearances.Count),
                "Схожесть — вероятностная оценка модели, не доказательство; появление — следственная версия, требует процессуальной проверки."),
            candidates);
    }

    private static BriefSection VerificationSection(
        IReadOnlyList<AppearanceRow> appearances, IReadOnlyDictionary<int, SearchCandidateRow> candidates, IReadOnlyDictionary<int, string> names)
    {
        // Решения стадий по уже подтверждённым или отозванным появлениям: кандидат завершён, «слепота» верификатора
        // (ТФ-ВЕР) здесь не нарушается.
        var rows = new List<IReadOnlyList<string>>();
        var total = 0;
        foreach (var appearance in appearances)
        {
            if (!candidates.TryGetValue(appearance.Id, out var candidate))
            {
                continue;
            }

            foreach (var decision in candidate.Decisions.OrderBy(d => d.DecidedAtUtc))
            {
                total++;
                if (rows.Count < BriefLimits.MaxRows)
                {
                    rows.Add(
                    [
                        Date(LocalDate(appearance.ConfirmedAtUtc)),
                        StageLabel(decision.Stage),
                        VerdictLabel(decision.Verdict),
                        Name(names, decision.SubjectId) ?? string.Empty,
                        DateTimeText(decision.DecidedAtUtc),
                        Clip(decision.Rationale),
                    ]);
                }
            }
        }

        // Решения читаются по кандидатам появлений из таблицы появлений (не больше BriefLimits.MaxRows); остальные —
        // числом, без молчаливого обрезания.
        var unread = appearances.Count(a => !candidates.ContainsKey(a.Id));
        return new BriefSection(
            "История верификаций",
            [new BriefFact("Решений по появлениям", Count(total))],
            new BriefTable(["Появление от", "Стадия", "Решение", "Кто", "Когда", "Обоснование"], rows, total),
            unread == 0
                ? null
                : string.Create(Ru, $"Появлений, решения по которым здесь не прочитаны (за пределами таблицы появлений или недоступны): {unread} — см. карточку фигуранта."));
    }

    private async Task<BriefSection> LinksSectionAsync(PersonRow person, AccessContext access, Grade grade, CancellationToken cancellationToken)
    {
        var casePersons = await persons.ListByCaseAsync(person.CaseId, access, cancellationToken);
        var linkTypes = (await references.ListAsync(ReferenceKind.LinkType, cancellationToken)).ToDictionary(r => r.Id, r => r.Name);
        var rows = new List<IReadOnlyList<string>>();

        if (person.LinkedToPersonId is { } target && casePersons.FirstOrDefault(p => p.Id == target) is { } linked)
        {
            grade.Add(linked.Classification);
            rows.Add([PersonName(linked), LinkName(linkTypes, person.LinkTypeId), "фигурант связан с ним"]);
        }

        foreach (var other in casePersons.Where(p => p.LinkedToPersonId == person.Id && p.Id != person.Id))
        {
            grade.Add(other.Classification);
            rows.Add([PersonName(other), LinkName(linkTypes, other.LinkTypeId), "связан с фигурантом"]);
        }

        foreach (var co in await persons.ListCoAppearancesAsync(person.Id, access, cancellationToken))
        {
            grade.Add(co.Person.Classification);
            rows.Add([PersonName(co.Person), "совместные появления", "материалов: " + Count(co.MaterialCount)]);
        }

        return new BriefSection(
            "Связи",
            [new BriefFact("Связей", Count(rows.Count))],
            new BriefTable(["Лицо", "Связь", "Пояснение"], rows.Take(BriefLimits.MaxRows).ToList(), rows.Count));
    }

    private async Task<BriefSection> IntersectionsSectionAsync(int personId, AccessContext access, Grade grade, CancellationToken cancellationToken)
    {
        // ТБ-084: о чужом деле — номер, вид, статус и значение реквизита; ФИО и материалы чужого фигуранта сюда не входят.
        var rows = await intersections.FindForPersonAsync(personId, access, cancellationToken) ?? [];
        foreach (var row in rows)
        {
            grade.Add(row.OtherCaseClassification);
        }

        return new BriefSection(
            "Пересечения с другими делами",
            [new BriefFact("Пересечений", Count(rows.Count))],
            new BriefTable(
                ["Реквизит", "Значение", "Дело", "Вид дела", "Статус дела", "Решение"],
                rows.Take(BriefLimits.MaxRows)
                    .Select(r => (IReadOnlyList<string>)
                    [
                        r.Kind.Label(),
                        r.OtherValue,
                        r.OtherCaseNumber,
                        r.OtherCaseKind.Label(),
                        r.OtherCaseStatus.Label(),
                        r.Decision?.Label() ?? "не рассмотрено",
                    ])
                    .ToList(),
                rows.Count),
            "О чужом деле — только номер, вид, статус и совпавший реквизит (ТБ-084).");
    }

    // ---------------- общие разделы ----------------

    // Поиски и их исходы: кандидаты считаются по исходам; незавершённые — одним числом «в работе», без чужих решений.
    private async Task<BriefSection> SearchesSectionAsync(
        string title, IReadOnlyList<SearchSessionRow> list, IReadOnlyDictionary<int, string> names,
        AccessContext access, Grade grade, CancellationToken cancellationToken)
    {
        var ordered = list.OrderBy(s => s.CreatedAt).ThenBy(s => s.Id).ToList();
        var rows = new List<IReadOnlyList<string>>();
        int confirmed = 0, rejected = 0, undetermined = 0, pending = 0, total = 0;
        foreach (var session in ordered)
        {
            grade.Add(session.Classification);
            if (rows.Count >= BriefLimits.MaxSessions)
            {
                continue;
            }

            var candidates = await sessions.ListCandidatesAsync(session.Id, access, cancellationToken);
            foreach (var candidate in candidates)
            {
                grade.Add(candidate.Classification);
            }

            var c = candidates.Count(x => x.Status == CandidateStatus.Confirmed);
            var r = candidates.Count(x => x.Status == CandidateStatus.Rejected);
            var u = candidates.Count(x => x.Status == CandidateStatus.Undetermined);
            var p = candidates.Count - c - r - u;
            (confirmed, rejected, undetermined, pending, total) = (confirmed + c, rejected + r, undetermined + u, pending + p, total + candidates.Count);
            rows.Add(
            [
                DateTimeText(session.CreatedAt),
                session.Origin == SessionOrigin.SystemSuggestion ? "предложено системой" : Name(names, session.RequestedByUserId) ?? string.Empty,
                ScopeLabel(session),
                Count(session.ProbeFaceIds is { Count: > 1 } probes ? probes.Count : 1),
                Count(candidates.Count),
                Count(c),
                Count(r),
                Count(u),
                Count(p),
            ]);
        }

        var facts = new List<BriefFact>
        {
            new("Поисков", Count(ordered.Count)),
            new("Кандидатов", Count(total)),
            new("Подтверждено", Count(confirmed)),
            new("Отклонено", Count(rejected)),
            new("Не определено", Count(undetermined)),
            new("В работе", Count(pending)),
        };
        return new BriefSection(
            title,
            facts,
            new BriefTable(
                ["Дата", "Кто", "Область", "Эталонов", "Кандидатов", "Подтверждено", "Отклонено", "Не определено", "В работе"],
                rows,
                ordered.Count),
            ordered.Count > BriefLimits.MaxSessions
                ? string.Create(Ru, $"Исходы кандидатов посчитаны по первым {BriefLimits.MaxSessions} поискам.")
                : null);
    }

    // ---------------- помощники ----------------

    private async Task<IReadOnlyDictionary<int, string>> NamesAsync(CancellationToken cancellationToken) =>
        (await accounts.ListAsync(cancellationToken))
            .GroupBy(a => a.UserId)
            .ToDictionary(g => g.Key, g => g.First().DisplayName is { Length: > 0 } display ? display : g.First().UserName);

    private DateOnly Today() => DateOnly.FromDateTime(time.GetLocalNow().DateTime);

    private static bool InPeriod(DateOnly date, DateOnly from, DateOnly to) => date >= from && date <= to;

    private static string? Name(IReadOnlyDictionary<int, string> names, int? userId) =>
        userId is { } id ? names.GetValueOrDefault(id) ?? "сотрудник " + id.ToString(CultureInfo.InvariantCulture) : null;

    /// <summary>Имя фигуранта для справки: неустановленное лицо — «неустановленное лицо N».</summary>
    public static string PersonName(PersonRow person)
    {
        ArgumentNullException.ThrowIfNull(person);
        return person.IsUnidentified && person.UnidentifiedNumber is { } number
            && (string.IsNullOrWhiteSpace(person.DisplayName) || person.DisplayName.Contains('№', StringComparison.Ordinal))
            ? "неустановленное лицо " + number.ToString(CultureInfo.InvariantCulture)
            : person.DisplayName;
    }

    private static string LinkName(Dictionary<int, string> types, int? typeId) =>
        typeId is { } id && types.TryGetValue(id, out var name) ? name : "связь";

    private static void Add(List<BriefFact> facts, string label, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            facts.Add(new BriefFact(label, value.Trim()));
        }
    }

    private static string Count(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Date(DateOnly date) => date.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);

    // Время из БД — UTC; без вида считается UTC (timestamptz), показывается по часам сервера, как в карточках.
    private static DateTime Local(DateTime utc) =>
        (utc.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(utc, DateTimeKind.Utc) : utc).ToLocalTime();

    private static DateOnly LocalDate(DateTime utc) => DateOnly.FromDateTime(Local(utc));

    private static string DateTimeText(DateTime utc) => Local(utc).ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture);

    private static string Clip(string? text)
    {
        var value = string.Join(' ', (text ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return value.Length <= BriefLimits.MaxCellLength ? value : BriefTexts.Cut(value, BriefLimits.MaxCellLength - 1) + "…";
    }

    private static string MediaKindLabel(MediaAssetRow asset) => asset.Kind switch
    {
        MediaKind.Video => "видео",
        MediaKind.Audio => "аудио",
        _ => asset.IsEditedCopy ? "фото, обработанная копия" : "фото",
    };

    private static string ScopeLabel(SearchSessionRow session) => session.Scope switch
    {
        // ADR-0044: перечень дел области не раскрывается — только вид и число.
        SearchScopeKind.CurrentCase => "своё дело",
        SearchScopeKind.SelectedCases => "выбранные дела (" + Count(session.CaseIds.Count) + ")",
        _ => "все доступные дела (" + Count(session.CaseIds.Count) + ")",
    };

    private static string StageLabel(VerificationStage stage) => stage switch
    {
        VerificationStage.Expert => "эксперт",
        VerificationStage.Verifier => "верификатор",
        _ => "руководитель",
    };

    private static string VerdictLabel(VerificationVerdict verdict) => verdict switch
    {
        VerificationVerdict.Confirmed => "подтверждено",
        VerificationVerdict.Rejected => "отклонено",
        _ => "не определено",
    };

    /// <summary>Момент записи «мм:сс» или «ч:мм:сс».</summary>
    public static string Clock(long milliseconds)
    {
        var span = TimeSpan.FromMilliseconds(Math.Max(0, milliseconds));
        return span.TotalHours >= 1
            ? ((int)span.TotalHours).ToString(CultureInfo.InvariantCulture) + ":" + span.ToString(@"mm\:ss", CultureInfo.InvariantCulture)
            : span.ToString(@"mm\:ss", CultureInfo.InvariantCulture);
    }

    private static string Moment(long? frameMs, FaceTrackSpan? span) =>
        span is { } s && s.EndMs > s.StartMs
            ? Clock(s.StartMs) + "–" + Clock(s.EndMs)
            : frameMs is { } ms ? Clock(ms) : string.Empty;

    // ТЭ-006/ADR-0038: время записи — сведения из файла, а не дата съёмки; поэтому «≈» и пометка.
    private static string RecordedMoment(MediaAssetRow? asset, long? frameMs) =>
        asset?.RecordedAt is { } recorded
            ? "≈ " + recorded.AddMilliseconds(frameMs ?? 0).LocalDateTime.ToString("dd.MM.yyyy HH:mm:ss", CultureInfo.InvariantCulture)
            : string.Empty;

    private static string ReportText(CaseReportContent content)
    {
        var parts = new List<string>();
        foreach (var e in content.Events ?? [])
        {
            var head = string.Join(" — ", new[] { e.Time, e.Place }.Where(v => !string.IsNullOrWhiteSpace(v)));
            var tail = new List<string>();
            if (!string.IsNullOrWhiteSpace(e.Description))
            {
                tail.Add(e.Description.Trim());
            }

            if (!string.IsNullOrWhiteSpace(e.Persons))
            {
                tail.Add("лица — " + e.Persons.Trim());
            }

            if (!string.IsNullOrWhiteSpace(e.Vehicles))
            {
                tail.Add("транспорт — " + e.Vehicles.Trim());
            }

            parts.Add((head.Length > 0 ? head + ": " : string.Empty) + string.Join("; ", tail));
        }

        Section(parts, "Установочные данные", content.Identity);
        Section(parts, "Адреса", content.Addresses);
        Section(parts, "Род занятий", content.Occupation);
        Section(parts, "Семейные и иные связи", content.Family);
        Section(parts, "Характеризующие материалы", content.Characterizing);
        Section(parts, "Вывод", content.Conclusion);
        return string.Join(". ", parts.Where(p => p.Length > 0));
    }

    private static void Section(List<string> parts, string title, string? text)
    {
        if (!string.IsNullOrWhiteSpace(text))
        {
            parts.Add(title + ": " + text.Trim());
        }
    }

    /// <summary>Гриф справки — наибольший из встреченных (только растёт).</summary>
    private sealed class Grade(short initial)
    {
        public short Value { get; private set; } = ClassificationLevels.Normalize(initial);

        public void Add(short classification) => Value = Math.Max(Value, ClassificationLevels.Normalize(classification));
    }
}
