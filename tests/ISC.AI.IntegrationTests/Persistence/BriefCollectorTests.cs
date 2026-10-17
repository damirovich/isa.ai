using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ISC.AI.Abstractions.BackgroundTasks;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.DocFlow.Data;
using ISC.AI.Modules.DocFlow.Domain.Enums;
using ISC.AI.Modules.Media.Data;
using ISC.AI.Modules.Media.Data.Entities;
using ISC.AI.Modules.Media.Domain.Model;
using ISC.AI.Modules.Media.Domain.Services;
using ISC.AI.Persistence.Entities;
using ISC.AI.Persistence.Security;
using ISC.AI.Profile.Investigation.Application.Features.Briefs;
using ISC.AI.Profile.Investigation.Application.Features.CaseDocuments;
using ISC.AI.Profile.Investigation.Data;
using ISC.AI.Profile.Investigation.Domain.Enums;
using ISC.AI.Profile.Investigation.Domain.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Shouldly;
using Testcontainers.PostgreSql;
using Xunit;

namespace ISC.AI.IntegrationTests.Persistence;

/// <summary>
/// Сбор фактов справок по делу и по фигуранту (ТФ-ОТЧ-01/02, ADR-0053) на настоящей БД: схемы <c>core</c>, <c>docflow</c>,
/// <c>media</c> и <c>investigation</c> в одном контейнере, настоящие хранилища и решётка профиля — как в хосте, без
/// DI-контейнера. Проверяется: справку получает ровно тот, кто видит карточку (ТБ-020/021/071), остальным — null,
/// неотличимо от несуществующего; разделы и факты собраны из хранилищ (фигуранты, носители, документы, основания, поиски
/// и сводки за период, адреса, транспорт, появления с историей верификаций, поиски по эталонам, связи, пересечения в
/// объёме ТБ-084); гриф справки — максимум грифов ровно тех сведений, что в неё вошли (ТБ-033): строки выше допуска, вне
/// периода, снятые и не входящие в справку гриф не поднимают. Модель здесь не участвует — факты считает код.
/// </summary>
/// <remarks>
/// Требуется Docker. Контейнер — на тест (xUnit создаёт экземпляр класса на каждый тест), «мир» дел засевается заново
/// в каждом тесте одним и тем же <see cref="SeedWorldAsync"/>. Часы сборщика зафиксированы (<see cref="Now"/>): период
/// по умолчанию «со дня открытия по сегодня» не зависит от даты прогона.
/// </remarks>
[Trait("Category", "Gate")]
public sealed class BriefCollectorTests : IAsyncLifetime
{
    // Субъекты без учётной записи ядра — роль ищется по числовому идентификатору (как в остальных тестах «Следствия»).
    private const int AdminId = 1001;
    private const int StrangerId = 1002;   // Следователь: не ведущий и не автор дела А
    private const int HeadId = 1003;
    private const int NoRoleId = 1004;
    private const int VerifierRoleId = 1005;
    private const int OtherInvestigatorId = 1009; // ведущий дела Б (роль не нужна)

    private const string CaseNumberA = "А-1/2026";
    private const string CaseNumberB = "Б-2/2026";
    private const string Plate = "01KG123ABC";
    private const string DivisionName = "Первый отдел";
    private const string LeadName = "Петров Пётр Петрович";
    private const string ExpertName = "Экспертов Эдуард";
    private const string VerifierName = "Верификаторов Виктор";

    /// <summary>«Сейчас» сборщика: 16.10.2026, полдень UTC — в любом поясе от −11 до +11 это та же дата.</summary>
    private static readonly DateTimeOffset Now = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset VideoRecordedAt = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);

    private readonly PostgreSqlContainer _postgres = TestPostgres.Create();
    private readonly string _files = Path.Combine(Path.GetTempPath(), "iscai-brief-tests", Guid.NewGuid().ToString("N"));

    private CoreContextFactory _core = null!;
    private MediaContextFactory _media = null!;
    private InvestigationContextFactory _investigation = null!;
    private DocFlowContextFactory _docflow = null!;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        var connection = _postgres.GetConnectionString();
        _core = new CoreContextFactory(connection);
        _media = new MediaContextFactory(connection);
        _investigation = new InvestigationContextFactory(connection);
        _docflow = new DocFlowContextFactory(connection);
        await using (var db = _core.CreateDbContext())
        {
            await db.Database.MigrateAsync();
        }

        await using (var db = _media.CreateDbContext())
        {
            await db.Database.MigrateAsync();
        }

        await using (var db = _docflow.CreateDbContext())
        {
            await db.Database.MigrateAsync();
        }

        await InvestigationTestKit.MigrateAsync(_investigation);
    }

    public async Task DisposeAsync()
    {
        await _postgres.DisposeAsync();
        if (Directory.Exists(_files))
        {
            Directory.Delete(_files, recursive: true);
        }
    }

    [Fact(DisplayName = "ТБ-020/021/071: справку по делу и по фигуранту получает только тот, кто видит карточку; чужое дело, гриф выше допуска, чужое подразделение, нет роли или права «Дашборд и реестр дел» — null, как у несуществующего")]
    public async Task Lattice_matches_cards()
    {
        var w = await SeedWorldAsync();
        var collector = CreateCollector();
        var ct = CancellationToken.None;

        async Task<(bool Case, bool Person)> SeesAsync(AccessContext access) =>
            (await collector.CaseAsync(w.CaseA, null, null, access, ct) is not null,
             await collector.PersonAsync(w.PersonP, access, ct) is not null);

        var lead = InvestigationTestKit.Access(w.LeadId, ClassificationLevels.Max, w.DivisionId);
        var verifier = InvestigationTestKit.Access(VerifierRoleId, ClassificationLevels.Max, w.DivisionId);

        // Видят карточку — получают справку: Администратор, ведущий следователь, Руководитель и Верификатор (без сужения
        // по владению, floor допуска — тот же).
        (await SeesAsync(w.Admin)).ShouldBe((true, true));
        (await SeesAsync(lead)).ShouldBe((true, true));
        (await SeesAsync(InvestigationTestKit.Access(HeadId, ClassificationLevels.Max, w.DivisionId))).ShouldBe((true, true));
        (await SeesAsync(verifier)).ShouldBe((true, true));

        // Не видят карточку — null для обеих справок: следователь не на этом деле, субъект без роли, гриф дела выше
        // допуска, подразделение вне допуска.
        (await SeesAsync(InvestigationTestKit.Access(StrangerId, ClassificationLevels.Max, w.DivisionId))).ShouldBe((false, false));
        (await SeesAsync(InvestigationTestKit.Access(NoRoleId, ClassificationLevels.Max, w.DivisionId))).ShouldBe((false, false));
        (await SeesAsync(InvestigationTestKit.Access(AdminId, ClassificationLevels.Unclassified, w.DivisionId))).ShouldBe((false, false));
        (await SeesAsync(InvestigationTestKit.Access(AdminId, ClassificationLevels.Max, w.DivisionId + 1000))).ShouldBe((false, false));

        // Фигурант чужого дела ведущему дела А не выдаётся, хотя госномер у него тот же, что у фигуранта дела А:
        // пересечение (ТБ-084) — не доступ к чужой карточке.
        (await collector.PersonAsync(w.PersonR, lead, ct)).ShouldBeNull();
        (await collector.PersonAsync(w.PersonR, w.Admin, ct)).ShouldNotBeNull();

        // Несуществующие дело и фигурант — тот же null.
        (await collector.CaseAsync(w.CaseB + 1000, null, null, w.Admin, ct)).ShouldBeNull();
        (await collector.PersonAsync(w.PersonR + 1000, w.Admin, ct)).ShouldBeNull();

        // Снятое у роли право «Дашборд и реестр дел» (матрица доступа, ADR-0032) действует со следующего запроса.
        await new UserRoleStore(_core, _investigation).ApplyPermissionChangesAsync(
            [new RolePermissionChange(InvestigationRole.Verifier, InvestigationPermissions.CasesView, false)], changedByUserId: AdminId);
        (await SeesAsync(verifier)).ShouldBe((false, false));
    }

    [Fact(DisplayName = "ТФ-ОТЧ-02: справка по делу — разделы по порядку, реквизиты дела, фигуранты («неустановленное лицо N»), носители, документы, основания; поиски и сводки — только за период, снятые с действия — нет")]
    public async Task Case_brief_sections_facts_and_period()
    {
        var w = await SeedWorldAsync();
        var collector = CreateCollector();
        var ct = CancellationToken.None;

        var brief = (await collector.CaseAsync(w.CaseA, null, null, w.Admin, ct)).ShouldNotBeNull();

        brief.Kind.ShouldBe(BriefKind.Case);
        brief.TargetId.ShouldBe(w.CaseA);
        brief.CaseId.ShouldBe(w.CaseA);
        brief.Title.ShouldBe("Справка по делу № " + CaseNumberA);

        // Период по умолчанию — со дня открытия дела (01.09.2026) по «сегодня» сборщика.
        brief.From.ShouldBe(new DateOnly(2026, 9, 1));
        brief.To.ShouldBe(new DateOnly(2026, 10, 16));
        brief.Subtitle.ShouldBe("«Дело " + CaseNumberA + "» · период 01.09.2026 — 16.10.2026");
        brief.CollectedAtUtc.ShouldBe(Now.UtcDateTime);
        brief.DivisionId.ShouldBe(w.DivisionId);
        brief.Classification.ShouldBe(ClassificationLevels.ForOfficialUse);

        brief.Sections.Select(s => s.Title).ShouldBe(
        [
            "Дело",
            "Фигуранты",
            "Материалы дела: носители",
            "Документы дела",
            "Основания поиска",
            "Поиски по лицу за период",
            "Сводки и справки по бланку за период",
        ]);

        // Реквизиты дела: вид и статус — подписями, ведущий — именем учётной записи, подразделение — из справочника.
        FactLines(Section(brief, "Дело")).ShouldBe(
        [
            "Номер: " + CaseNumberA,
            "Название: Дело " + CaseNumberA,
            "Вид: Уголовное дело",
            "Отдел: Оперативное наблюдение",
            "Статус: В производстве",
            "Открыто: 01.09.2026",
            "Ведущий: " + LeadName,
            "Подразделение: " + DivisionName,
            "Гриф дела: ДСП",
        ]);

        // Фигуранты дела А (фигурант дела Б сюда не попадает); неустановленное лицо — без «№». Подтверждённые
        // появления — без отозванных, эталоны — действующие.
        var persons = Section(brief, "Фигуранты");
        FactValue(persons, "Фигурантов").ShouldBe("4");
        Lines(persons).ShouldBe(
        [
            "Иванов Иван Иванович | Объект |  | 1 | 1",
            "неустановленное лицо 1 | Иная |  | 0 | 0",
            "Сидоров Семён | Связь | брат объекта | 0 | 0",
            "Кузнецов Кузьма | Иная |  | 1 | 0",
        ]);
        persons.Table.ShouldNotBeNull().Rows.ShouldAllBe(r => !r[0].Contains('№'));

        // Носители: по видам, лица и расшифровки — по строкам каталога под решёткой; место — из привязки к делу.
        var media = Section(brief, "Материалы дела: носители");
        FactLines(media).ShouldBe(
        [
            "Носителей: 2",
            "Видео: 1",
            "Аудио: 0",
            "Фото: 1",
            "Найдено лиц на носителях: 4",
            "Расшифровано записей: 0",
        ]);
        var mediaLines = Lines(media);
        mediaLines.Count.ShouldBe(2);
        mediaLines.ShouldContain("видео.mp4 | видео | 20.09.2026 | рынок | 3 | ");
        mediaLines.ShouldContain("эталон.jpg | фото |  |  | 1 | ");

        var documents = Section(brief, "Документы дела");
        FactValue(documents, "Документов").ShouldBe("1");
        Lines(documents).ShouldBe(["15-с | 10.09.2026 | Справка | Справка о проверке адреса"]);

        var authorizations = Section(brief, "Основания поиска");
        FactValue(authorizations, "Оснований").ShouldBe("1");
        Lines(authorizations).ShouldBe(["Поручение следователя | Поручение № 7 | 02.09.2026 | 31.12.2026"]);

        // Поиски за период: три сессии октября; сессия 2025 года вне периода — ни строкой, ни в счётчиках. Кандидаты —
        // по исходам; область «все доступные дела» — видом и числом, без перечня дел (ADR-0044).
        var searches = Section(brief, "Поиски по лицу за период");
        FactLines(searches).ShouldBe(
        [
            "Поисков: 3",
            "Кандидатов: 4",
            "Подтверждено: 1",
            "Отклонено: 1",
            "Не определено: 1",
            "В работе: 1",
        ]);
        Lines(searches).ShouldBe(
        [
            LocalText(Utc(2026, 10, 1, 12)) + " | " + LeadName + " | своё дело | 1 | 4 | 1 | 1 | 1 | 1",
            LocalText(Utc(2026, 10, 1, 13)) + " | предложено системой | своё дело | 1 | 0 | 0 | 0 | 0 | 0",
            LocalText(Utc(2026, 10, 2, 12)) + " | " + LeadName + " | все доступные дела (2) | 1 | 0 | 0 | 0 | 0 | 0",
        ]);
        string.Join("\n", Lines(searches)).ShouldNotContain(CaseNumberB);

        // Сводки за период: сводка 15.09 и справка 20.09; справка 15.08 — до открытия дела (вне периода), сводка 05.10
        // снята с действия — обеих нет ни строкой, ни в счётчиках.
        var reports = Section(brief, "Сводки и справки по бланку за период");
        FactLines(reports).ShouldBe(["Сводок ОН: 1", "Справок ОУ: 1"]);
        var reportRows = reports.Table.ShouldNotBeNull().Rows;
        reportRows.Count.ShouldBe(2);
        string.Join(" | ", reportRows[0]).ShouldBe("15.09.2026 | Сводка ОН | Иванов Иван Иванович | 14:30 — ул. Ленина, 15: вышел из дома");
        // Объект справки — неустановленное лицо: то же имя, что в таблице фигурантов, без «№».
        string.Join(" | ", reportRows[1]).ShouldBe("20.09.2026 | Справка ОУ | неустановленное лицо 1 | Вывод: проживает один");
        reports.Table.TotalRows.ShouldBe(2);

        // Явный период сужает только поиски и сводки; остальное — по делу целиком.
        var autumn = (await collector.CaseAsync(w.CaseA, new DateOnly(2026, 9, 16), new DateOnly(2026, 10, 16), w.Admin, ct)).ShouldNotBeNull();
        autumn.From.ShouldBe(new DateOnly(2026, 9, 16));
        autumn.Subtitle.ShouldEndWith("период 16.09.2026 — 16.10.2026");
        FactValue(Section(autumn, "Поиски по лицу за период"), "Поисков").ShouldBe("3");
        ReportDates(autumn).ShouldBe(["20.09.2026"]);
        FactValue(Section(autumn, "Фигуранты"), "Фигурантов").ShouldBe("4");

        var september = (await collector.CaseAsync(w.CaseA, new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30), w.Admin, ct)).ShouldNotBeNull();
        FactValue(Section(september, "Поиски по лицу за период"), "Поисков").ShouldBe("0");
        Lines(Section(september, "Поиски по лицу за период")).ShouldBeEmpty();
        ReportDates(september).ShouldBe(["15.09.2026", "20.09.2026"]);

        // Контроль: справка 15.08 исключена именно периодом — в своём периоде она есть.
        var august = (await collector.CaseAsync(w.CaseA, new DateOnly(2026, 8, 1), new DateOnly(2026, 8, 31), w.Admin, ct)).ShouldNotBeNull();
        ReportDates(august).ShouldBe(["15.08.2026"]);
        FactLines(Section(august, "Сводки и справки по бланку за период")).ShouldBe(["Сводок ОН: 0", "Справок ОУ: 1"]);
    }

    [Fact(DisplayName = "ТФ-ОТЧ-01: справка по фигуранту — разделы по порядку, анкета, адреса и транспорт, дело, появления с отрезком и отозванным, история верификаций, поиски по эталонам, связи и совместные появления, пересечения в объёме ТБ-084")]
    public async Task Person_brief_sections_and_facts()
    {
        var w = await SeedWorldAsync();
        var collector = CreateCollector();
        var ct = CancellationToken.None;

        var brief = (await collector.PersonAsync(w.PersonP, w.Admin, ct)).ShouldNotBeNull();

        brief.Kind.ShouldBe(BriefKind.Person);
        brief.TargetId.ShouldBe(w.PersonP);
        brief.CaseId.ShouldBe(w.CaseA);
        brief.Title.ShouldBe("Справка по фигуранту: Иванов Иван Иванович");
        brief.Subtitle.ShouldBe("Дело № " + CaseNumberA + " «Дело " + CaseNumberA + "»");
        brief.From.ShouldBeNull();
        brief.To.ShouldBeNull();
        brief.CollectedAtUtc.ShouldBe(Now.UtcDateTime);
        brief.DivisionId.ShouldBe(w.DivisionId);
        brief.Classification.ShouldBe(ClassificationLevels.ForOfficialUse);

        brief.Sections.Select(s => s.Title).ShouldBe(
        [
            "Фигурант",
            "Адреса",
            "Транспорт",
            "Дело фигуранта",
            "Подтверждённые появления",
            "История верификаций",
            "Поиски по эталонам фигуранта",
            "Связи",
            "Пересечения с другими делами",
        ]);

        FactLines(Section(brief, "Фигурант")).ShouldBe(
        [
            "Фигурант: Иванов Иван Иванович",
            "Псевдоним: Ваня",
            "Роль: Объект",
            "Дата рождения: 04.05.1990",
        ]);
        Lines(Section(brief, "Адреса")).ShouldBe(["Проживание | г. Бишкек, ул. Ленина, 15 | со слов соседей"]);
        Lines(Section(brief, "Транспорт")).ShouldBe([Plate + " | Toyota | Camry | белый | "]);
        FactLines(Section(brief, "Дело фигуранта")).ShouldBe(
        [
            "Номер: " + CaseNumberA,
            "Название: Дело " + CaseNumberA,
            "Вид: Уголовное дело",
            "Статус: В производстве",
            "Открыто: 01.09.2026",
            "Ведущий: " + LeadName,
        ]);

        // Появления: действующее — с отрезком трека в записи, временем записи по файлу «≈», схожестью (ru), экспертом и
        // верификатором и пометкой «следственная версия»; отозванное — в таблице с пометкой «отозвано».
        var appearances = Section(brief, "Подтверждённые появления");
        FactLines(appearances).ShouldBe(["Подтверждённых появлений: 1", "Отозванных: 1"]);
        var appearanceRows = appearances.Table.ShouldNotBeNull().Rows;
        appearanceRows.Count.ShouldBe(2);
        string.Join(" | ", appearanceRows.Single(r => r[7] == TwoPersonRule.ConfirmedMarker)).ShouldBe(string.Join(" | ",
            "03.10.2026",
            "видео.mp4 (видео)",
            "00:00–00:01",
            "≈ " + VideoRecordedAt.LocalDateTime.ToString("dd.MM.yyyy HH:mm:ss", CultureInfo.InvariantCulture),
            "93 %", // балл 0,87 по шкале ADR-0054
            ExpertName,
            VerifierName,
            TwoPersonRule.ConfirmedMarker));
        appearanceRows.Count(r => r[7].StartsWith("отозвано", StringComparison.Ordinal)).ShouldBe(1);

        // История верификаций — решения стадий по кандидату действующего появления, по времени.
        var verifications = Section(brief, "История верификаций");
        FactValue(verifications, "Решений по появлениям").ShouldBe("2");
        Lines(verifications).ShouldBe(
        [
            "03.10.2026 | эксперт | подтверждено | " + ExpertName + " | " + LocalText(Utc(2026, 10, 3, 9)) + " | совпадают черты лица",
            "03.10.2026 | верификатор | подтверждено | " + VerifierName + " | " + LocalText(Utc(2026, 10, 3, 10)) + " | подтверждаю",
        ]);

        // Поиски по эталонам: по лицу его эталона (в том числе 2025 года — у справки по фигуранту периода нет) и
        // предложение системы для него; поиск по лицу другого фигуранта сюда не входит.
        var searches = Section(brief, "Поиски по эталонам фигуранта");
        FactLines(searches).ShouldBe(
        [
            "Поисков: 3",
            "Кандидатов: 4",
            "Подтверждено: 1",
            "Отклонено: 1",
            "Не определено: 1",
            "В работе: 1",
        ]);
        var searchLines = Lines(searches);
        searchLines.Count.ShouldBe(3);
        searchLines[0].ShouldStartWith(LocalText(Utc(2025, 1, 15, 12)) + " | " + LeadName);
        searchLines.ShouldContain(l => l.Contains("предложено системой", StringComparison.Ordinal));
        searchLines.ShouldNotContain(l => l.Contains("все доступные дела", StringComparison.Ordinal));

        // Связи: связь дела (кем приходится — из справочника) и совместные появления на одном носителе.
        var links = Section(brief, "Связи");
        FactValue(links, "Связей").ShouldBe("2");
        Lines(links).ShouldBe(
        [
            "Сидоров Семён | Брат | связан с фигурантом",
            "Кузнецов Кузьма | совместные появления | материалов: 1",
        ]);

        // С другой стороны связи — «фигурант связан с ним»; совместность — взаимна.
        var linked = (await collector.PersonAsync(w.PersonL, w.Admin, ct)).ShouldNotBeNull();
        Lines(Section(linked, "Связи")).ShouldBe(["Иванов Иван Иванович | Брат | фигурант связан с ним"]);
        var coAppearing = (await collector.PersonAsync(w.PersonQ, w.Admin, ct)).ShouldNotBeNull();
        Lines(Section(coAppearing, "Связи")).ShouldBe(["Иванов Иван Иванович | совместные появления | материалов: 1"]);

        // Пересечение по госномеру с делом Б: номер, вид, статус чужого дела и значение реквизита; ни имени чужого
        // фигуранта, ни его материалов (ТБ-084).
        var intersections = Section(brief, "Пересечения с другими делами");
        FactValue(intersections, "Пересечений").ShouldBe("1");
        Lines(intersections).ShouldBe([$"Автотранспорт | {Plate} | {CaseNumberB} | Уголовное дело | В производстве | не рассмотрено"]);
        string.Join("\n", brief.Sections.SelectMany(s => s.Table?.Rows ?? []).Select(r => string.Join(" | ", r)))
            .ShouldNotContain("Петренко");

        // Неустановленное лицо — «неустановленное лицо N» и в шапке, и в разделе «Фигурант».
        var unidentified = (await collector.PersonAsync(w.PersonU, w.Admin, ct)).ShouldNotBeNull();
        unidentified.Title.ShouldBe("Справка по фигуранту: неустановленное лицо 1");
        FactValue(Section(unidentified, "Фигурант"), "Фигурант").ShouldBe("неустановленное лицо 1");
    }

    [Fact(DisplayName = "ТБ-033: гриф справки — максимум грифов вошедших сведений; строки выше допуска, вне периода, снятые и не входящие в справку гриф не поднимают")]
    public async Task Classification_is_max_of_included_facts_only()
    {
        var w = await SeedWorldAsync();
        var collector = CreateCollector();
        var ct = CancellationToken.None;

        async Task<short> CaseGradeAsync() =>
            (await collector.CaseAsync(w.CaseA, null, null, w.Admin, ct)).ShouldNotBeNull().Classification;
        async Task<short> PersonGradeAsync() =>
            (await collector.PersonAsync(w.PersonP, w.Admin, ct)).ShouldNotBeNull().Classification;

        // Всё засеяно под грифом дела (ДСП) — гриф обеих справок ДСП.
        (await CaseGradeAsync()).ShouldBe(ClassificationLevels.ForOfficialUse);
        (await PersonGradeAsync()).ShouldBe(ClassificationLevels.ForOfficialUse);

        // Справка по делу: каждое вошедшее сведение, поднятое до «совершенно секретно», поднимает гриф справки;
        // возвращённое — опускает.
        await ExpectRaisesAsync(CaseGradeAsync, "справка по делу",
        [
            ("фигурант дела", PersonLevel(w.PersonQ)),
            ("подтверждённое появление", AppearanceLevel(w.AppearanceA1)),
            ("носитель дела", AssetLevel(w.Image)),
            ("документ дела", DocumentLevel(w.DocumentId)),
            ("поиск за период", SessionLevel(w.SessionS1)),
            ("кандидат поиска за период", CandidateLevel(w.CandidatesS1[3])),
            ("сводка за период", ReportLevel(w.ReportInPeriod)),
        ]);

        // Не вошедшее в справку по делу гриф не поднимает, даже «особой важности» в пределах допуска.
        await ExpectIgnoredAsync(CaseGradeAsync, "справка по делу",
        [
            ("отозванное появление", AppearanceLevel(w.AppearanceA2)),
            ("справка вне периода", ReportLevel(w.ReportBeforePeriod)),
            ("снятая с действия сводка", ReportLevel(w.ReportInactive)),
            ("поиск вне периода", SessionLevel(w.SessionOld)),
            ("адрес фигуранта (в справке по делу его нет)", AddressLevel(w.AddressP)),
            ("транспорт фигуранта (в справке по делу его нет)", VehicleLevel(w.VehicleP)),
            ("чужое дело пересечения (в справке по делу его нет)", CaseLevel(w.CaseB)),
        ]);

        // Справка по фигуранту: адреса, транспорт, отозванное (оно в таблице), кандидат его появления, поиск по эталону
        // без периода, носитель появления, связь, совместно появлявшийся, чужое дело пересечения.
        await ExpectRaisesAsync(PersonGradeAsync, "справка по фигуранту",
        [
            ("адрес", AddressLevel(w.AddressP)),
            ("транспорт", VehicleLevel(w.VehicleP)),
            ("отозванное появление", AppearanceLevel(w.AppearanceA2)),
            ("кандидат появления", CandidateLevel(w.CandidatesS1[0])),
            ("поиск по эталону 2025 года", SessionLevel(w.SessionOld)),
            ("носитель появления", AssetLevel(w.Video)),
            ("связь фигуранта", PersonLevel(w.PersonL)),
            ("совместно появлявшийся", PersonLevel(w.PersonQ)),
            ("чужое дело пересечения", CaseLevel(w.CaseB)),
        ]);

        await ExpectIgnoredAsync(PersonGradeAsync, "справка по фигуранту",
        [
            ("сводка дела", ReportLevel(w.ReportInPeriod)),
            ("документ дела", DocumentLevel(w.DocumentId)),
            ("поиск по лицу другого фигуранта", SessionLevel(w.SessionOther)),
            ("фигурант дела без связи", PersonLevel(w.PersonU)),
            ("носитель эталона", AssetLevel(w.Image)),
        ]);

        // Строки выше допуска субъекта в справку не входят и гриф не поднимают (ТБ-020): адрес «секретно» субъекту с
        // допуском ДСП не показан, гриф его справки — ДСП; Администратор с полным допуском видит адрес и гриф «секретно».
        var dsp = InvestigationTestKit.Access(AdminId, ClassificationLevels.ForOfficialUse, w.DivisionId);
        await AddressLevel(w.AddressP)(ClassificationLevels.Secret);
        var limited = (await collector.PersonAsync(w.PersonP, dsp, ct)).ShouldNotBeNull();
        limited.Classification.ShouldBe(ClassificationLevels.ForOfficialUse);
        Section(limited, "Адреса").Table.ShouldNotBeNull().TotalRows.ShouldBe(0);
        (await PersonGradeAsync()).ShouldBe(ClassificationLevels.Secret);

        // То же у носителя в справке по делу: не виден — не посчитан и гриф не поднял.
        await AssetLevel(w.Image)(ClassificationLevels.Secret);
        var limitedCase = (await collector.CaseAsync(w.CaseA, null, null, dsp, ct)).ShouldNotBeNull();
        limitedCase.Classification.ShouldBe(ClassificationLevels.ForOfficialUse);
        FactValue(Section(limitedCase, "Материалы дела: носители"), "Носителей").ShouldBe("1");
        (await CaseGradeAsync()).ShouldBe(ClassificationLevels.Secret);

        // Чужое дело пересечения выше допуска — пересечения нет, гриф не растёт.
        await CaseLevel(w.CaseB)(ClassificationLevels.SpecialImportance);
        var topSecret = InvestigationTestKit.Access(AdminId, ClassificationLevels.TopSecret, w.DivisionId);
        var withoutB = (await collector.PersonAsync(w.PersonP, topSecret, ct)).ShouldNotBeNull();
        Section(withoutB, "Пересечения с другими делами").Table.ShouldNotBeNull().TotalRows.ShouldBe(0);
        withoutB.Classification.ShouldBe(ClassificationLevels.Secret);
        (await PersonGradeAsync()).ShouldBe(ClassificationLevels.SpecialImportance);
    }

    // ---------- «мир» дел ----------

    /// <summary>
    /// Засевает два дела одного подразделения настоящими хранилищами (и строками «Медиа» напрямую — как
    /// <c>CasePurgeTests</c>). Дело А (гриф ДСП, ведёт следователь): фигуранты — объект Иванов (анкета, адрес, транспорт,
    /// эталон), неустановленное лицо № 1, связь Сидоров (брат Иванова), Кузнецов (совместно появлялся); основание поиска;
    /// видео (3 лица, два — один трек) и фото эталона; документ; четыре поиска (три в октябре 2026, один — в 2025);
    /// появления Иванова (действующее и отозванное) и Кузнецова; четыре сводки/справки (в периоде, до открытия дела,
    /// снятая с действия, по неустановленному лицу). Дело Б: фигурант Петренко с тем же госномером, что у Иванова.
    /// </summary>
    private async Task<World> SeedWorldAsync()
    {
        var lead = await AddUserAsync("petrov", LeadName);
        var expert = await AddUserAsync("expert", ExpertName);
        var verifier = await AddUserAsync("verifier", VerifierName);
        await InvestigationTestKit.AssignRolesAsync(_investigation,
            (AdminId, InvestigationRole.Administrator),
            (lead, InvestigationRole.Investigator),
            (StrangerId, InvestigationRole.Investigator),
            (HeadId, InvestigationRole.Head),
            (VerifierRoleId, InvestigationRole.Verifier));

        var division = await new DivisionAdminStore(_investigation, _core).CreateAsync(DivisionName, "01", null, CaseDirection.Surveillance);
        var admin = InvestigationTestKit.Access(AdminId, ClassificationLevels.Max, division);
        var policy = new InvestigationAccessPolicy(_investigation);
        var roles = new UserRoleStore(_core, _investigation);
        var cases = InvestigationTestKit.CreateCaseStore(_investigation, _core);
        var persons = InvestigationTestKit.CreatePersonStore(_investigation, _core);
        var requisites = new PersonRequisiteStore(_investigation, policy, roles);

        var caseA = await CreateCaseAsync(cases, CaseNumberA, division, lead, admin);
        var caseB = await CreateCaseAsync(cases, CaseNumberB, division, OtherInvestigatorId, admin);

        // Фигуранты дела А и связь «брат» из справочника.
        var personP = await CreatePersonAsync(persons, new PersonDraft(
            caseA, "Иванов Иван Иванович", false, null, null, PersonRole.Target,
            new PersonQuestionnaire(BirthDate: new DateOnly(1990, 5, 4), Alias: "Ваня")), admin);
        var personU = await CreatePersonAsync(persons, new PersonDraft(caseA, null, true, null, null), admin);
        var (typeResult, brother) = await new ReferenceStore(_investigation).CreateAsync(ReferenceKind.LinkType, "Брат", null, 1);
        typeResult.ShouldBe(ReferenceWriteResult.Ok);
        var personL = await CreatePersonAsync(persons, new PersonDraft(
            caseA, "Сидоров Семён", false, "брат объекта", null, PersonRole.Link, null, personP, brother), admin);
        var personQ = await CreatePersonAsync(persons, new PersonDraft(caseA, "Кузнецов Кузьма", false, null, null), admin);

        var (addressResult, addressP) = await requisites.SaveAddressAsync(
            personP, null, new PersonAddressDraft(AddressKind.Residence, "г. Бишкек, ул. Ленина, 15", "со слов соседей"), admin);
        addressResult.ShouldBe(PersonWriteResult.Ok);
        var (vehicleResult, vehicleP) = await requisites.SaveVehicleAsync(
            personP, null, new PersonVehicleDraft(Plate, "Toyota", "Camry", "белый", null), admin);
        vehicleResult.ShouldBe(PersonWriteResult.Ok);

        // Дело Б: тот же госномер у его фигуранта — пересечение (ТБ-084).
        var personR = await CreatePersonAsync(persons, new PersonDraft(caseB, "Петренко Роман", false, null, null), admin);
        (await requisites.SaveVehicleAsync(personR, null, new PersonVehicleDraft(Plate, "Toyota", null, null, null), admin))
            .Result.ShouldBe(PersonWriteResult.Ok);

        (await cases.AddAuthorizationAsync(
            new SearchAuthorizationDraft(caseA, AuthorizationKind.InvestigatorOrder, "Поручение № 7", new DateOnly(2026, 9, 2), lead,
                new DateOnly(2026, 12, 31), null),
            admin)).Result.ShouldBe(CaseWriteResult.Ok);

        // Носители дела А: видео (лица на 0, 1 и 2 с; первые два — один трек) и фото эталона.
        (int Id, IReadOnlyList<int> Faces) video, image;
        await using (var db = _media.CreateDbContext())
        {
            video = await SeedAssetAsync(db, "видео.mp4", MediaKind.Video, division,
                new DateTimeOffset(2026, 9, 20, 12, 0, 0, TimeSpan.Zero), VideoRecordedAt, (0, 1), (1000, 1), (2000, 2));
            image = await SeedAssetAsync(db, "эталон.jpg", MediaKind.Image, division, null, null, (0, null));
        }

        (await cases.LinkMediaAsync(caseA, video.Id, "рынок", lead)).ShouldBe(CaseWriteResult.Ok);
        (await cases.LinkMediaAsync(caseA, image.Id, null, lead)).ShouldBe(CaseWriteResult.Ok);
        (await persons.AddReferencePhotoAsync(
            new ReferencePhotoDraft(personP, image.Id, image.Faces[0], 0.9f, "фото эталона", "Поручение № 7", null, lead), null, admin))
            .Result.ShouldBe(PersonWriteResult.Ok);

        // Поиски дела А. S1 — по эталону Иванова, четыре кандидата; S2 — предложение системы для Иванова; S3 — по лицу
        // Кузнецова во всех доступных делах; S0 — по эталону Иванова в 2025 году (вне периода справки по делу).
        (int Id, IReadOnlyList<int> Candidates) s1;
        int s3, s0;
        await using (var db = _media.CreateDbContext())
        {
            s1 = await SeedSessionAsync(db, caseA, Utc(2026, 10, 1, 12), image.Faces[0], lead, null, SearchScopeKind.CurrentCase, [caseA], division,
                (video.Faces[0], video.Id), (video.Faces[1], video.Id), (video.Faces[1], video.Id), (video.Faces[2], video.Id));
            await SeedSessionAsync(db, caseA, Utc(2026, 10, 1, 13), image.Faces[0], null, personP, SearchScopeKind.CurrentCase, [caseA], division);
            s3 = (await SeedSessionAsync(db, caseA, Utc(2026, 10, 2, 12), video.Faces[2], lead, null, SearchScopeKind.AllAccessibleCases,
                [caseA, caseB], division)).Id;
            s0 = (await SeedSessionAsync(db, caseA, Utc(2025, 1, 15, 12), image.Faces[0], lead, null, SearchScopeKind.CurrentCase, [caseA], division)).Id;
        }

        // Исходы кандидатов S1: подтверждён двумя сотрудниками, отклонён, «не определено», в работе.
        var sessions = new SearchSessionStore(_media, policy);
        await sessions.RecordDecisionAsync(s1.Candidates[0],
            new VerificationDecision(expert, VerificationStage.Expert, VerificationVerdict.Confirmed, "совпадают черты лица", Utc(2026, 10, 3, 9)),
            CandidateStatus.PendingVerifier, null);
        await sessions.RecordDecisionAsync(s1.Candidates[0],
            new VerificationDecision(verifier, VerificationStage.Verifier, VerificationVerdict.Confirmed, "подтверждаю", Utc(2026, 10, 3, 10)),
            CandidateStatus.Confirmed, personP);
        await sessions.RecordDecisionAsync(s1.Candidates[1],
            new VerificationDecision(expert, VerificationStage.Expert, VerificationVerdict.Rejected, "другой человек", Utc(2026, 10, 3, 9)),
            CandidateStatus.Rejected, null);
        await sessions.RecordDecisionAsync(s1.Candidates[2],
            new VerificationDecision(expert, VerificationStage.Expert, VerificationVerdict.Undetermined, "плохое качество", Utc(2026, 10, 3, 9)),
            CandidateStatus.Undetermined, null);

        // Появления: Иванов — по подтверждённому кандидату и ещё одно (отозвано Администратором); Кузнецов — на том же видео.
        var appearanceA1 = await persons.AddAppearanceAsync(new AppearanceDraft(
            personP, caseA, video.Id, video.Faces[0], 0, 0, s1.Id, s1.Candidates[0], 0.87, Utc(2026, 10, 3, 12), expert, verifier,
            ClassificationLevels.ForOfficialUse, division));
        var appearanceA2 = await persons.AddAppearanceAsync(new AppearanceDraft(
            personP, caseA, video.Id, video.Faces[1], 1, 1000, s1.Id, 990_001, 0.55, Utc(2026, 10, 4, 12), expert, verifier,
            ClassificationLevels.ForOfficialUse, division));
        (await persons.RevokeAppearanceAsync(appearanceA2, "ошибочное опознание", admin)).ShouldBe(AppearanceRevokeResult.Ok);
        await persons.AddAppearanceAsync(new AppearanceDraft(
            personQ, caseA, video.Id, video.Faces[2], 2, 2000, s3, 990_002, 0.8, Utc(2026, 10, 5, 12), expert, verifier,
            ClassificationLevels.ForOfficialUse, division));

        // Сводки и справки по бланку.
        var reports = new CaseReportStore(_investigation, policy, roles, new CaseReportOptions(), TimeProvider.System);
        var inPeriod = await CreateReportAsync(reports, new CaseReportDraft(caseA, CaseReportKind.SummaryOn, new DateOnly(2026, 9, 15), personP,
            new CaseReportContent(Events: [new SummaryEvent("14:30", "ул. Ленина, 15", "вышел из дома", null, null)])), admin);
        var beforePeriod = await CreateReportAsync(reports, new CaseReportDraft(caseA, CaseReportKind.ReferenceUn, new DateOnly(2026, 8, 15), null,
            new CaseReportContent(Conclusion: "до открытия дела")), admin);
        var inactive = await CreateReportAsync(reports, new CaseReportDraft(caseA, CaseReportKind.SummaryOn, new DateOnly(2026, 10, 5), personP,
            new CaseReportContent(Events: [new SummaryEvent("09:00", "рынок", "снятая сводка", null, null)])), admin);
        (await reports.SetActiveAsync(inactive, false, admin)).ShouldBe(CaseReportWriteResult.Ok);
        await CreateReportAsync(reports, new CaseReportDraft(caseA, CaseReportKind.ReferenceUn, new DateOnly(2026, 9, 20), personU,
            new CaseReportContent(Conclusion: "проживает один")), admin);

        var documentId = await ImportDocumentAsync(new CaseScopeItem(caseA, CaseNumberA, "Дело " + CaseNumberA, ClassificationLevels.ForOfficialUse, division), admin);

        return new World(
            admin, division, lead, caseA, caseB, personP, personU, personL, personQ, personR, addressP, vehicleP,
            video.Id, image.Id, documentId, s1.Id, s0, s3, s1.Candidates, appearanceA1, appearanceA2,
            inPeriod, beforePeriod, inactive);
    }

    private BriefCollector CreateCollector()
    {
        var policy = new InvestigationAccessPolicy(_investigation);
        var roles = new UserRoleStore(_core, _investigation);
        var catalog = new MediaCatalog(_media, policy);
        return new BriefCollector(
            InvestigationTestKit.CreateCaseStore(_investigation, _core),
            InvestigationTestKit.CreatePersonStore(_investigation, _core),
            new PersonRequisiteStore(_investigation, policy, roles),
            new IntersectionStore(_investigation, policy, roles, catalog),
            new CaseDocumentStore(_investigation, policy, roles),
            new DocumentLookup(_docflow, policy),
            new CaseReportStore(_investigation, policy, roles, new CaseReportOptions(), TimeProvider.System),
            new SearchSessionStore(_media, policy),
            catalog,
            new UserAccountStore(_core),
            new ReferenceStore(_investigation),
            new DivisionAdminStore(_investigation, _core),
            new FixedClock(Now));
    }

    private async Task<int> AddUserAsync(string login, string name)
    {
        await using var db = _core.CreateDbContext();
        var user = new AppUserEntity { UserName = login, DisplayName = name };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

    private static async Task<int> CreateCaseAsync(CaseStore cases, string number, int division, int investigator, AccessContext admin)
    {
        var (result, caseId) = await cases.CreateAsync(InvestigationTestKit.Draft(number, division, ClassificationLevels.ForOfficialUse, investigator), admin);
        result.ShouldBe(CaseWriteResult.Ok);
        return caseId;
    }

    private static async Task<int> CreatePersonAsync(PersonStore persons, PersonDraft draft, AccessContext admin)
    {
        var (result, personId) = await persons.CreateAsync(draft, admin);
        result.ShouldBe(PersonWriteResult.Ok);
        return personId;
    }

    private static async Task<int> CreateReportAsync(CaseReportStore reports, CaseReportDraft draft, AccessContext admin)
    {
        var (result, reportId) = await reports.CreateAsync(draft, admin);
        result.ShouldBe(CaseReportWriteResult.Ok);
        return reportId;
    }

    /// <summary>Документ дела — тем же путём, что живые документы (приём архива, ADR-0042), как в <c>CaseChatScopeTests</c>.</summary>
    private async Task<int> ImportDocumentAsync(CaseScopeItem caseItem, AccessContext admin)
    {
        var types = new DocumentTypeStore(_docflow);
        var type = (await types.CreateAsync("Справка", DocumentGroup.Storage, isActive: true)).ShouldNotBeNull();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["DocFlow:Storage:BasePath"] = _files })
            .Build();
        var documents = new DocumentStore(_docflow, new LocalDocFlowFileStorage(configuration), new AllowAllAccessPolicy(), TestUserDirectory.AllowAll);
        var policy = new InvestigationAccessPolicy(_investigation);
        var links = new CaseDocumentStore(_investigation, policy, new UserRoleStore(_core, _investigation));
        var sink = new ArchiveDocumentSink(documents, types, links, new NoQueue());
        var result = await sink.ImportAsync(
            new ArchiveDocumentRequest(caseItem, "15-с.txt", "text/plain", [0x31], type, "15-с", new DateOnly(2026, 9, 10),
                "Справка о проверке адреса", null, "тест", ExistingDocumentId: null),
            admin,
            _ => Task.CompletedTask);
        return result.DocumentId.ShouldNotBeNull();
    }

    /// <summary>Носитель с лицами: у видео — кадр на каждое лицо (момент и трек заданы), у фото — лица без кадра.</summary>
    private static async Task<(int Id, IReadOnlyList<int> Faces)> SeedAssetAsync(
        MediaDbContext db, string fileName, MediaKind kind, int divisionId, DateTimeOffset? capturedAt, DateTimeOffset? recordedAt,
        params (long TimestampMs, int? TrackId)[] faces)
    {
        var asset = new MediaAsset
        {
            Kind = kind,
            OriginalFileName = fileName,
            StoredFileName = Guid.NewGuid().ToString("N") + Path.GetExtension(fileName),
            ContentType = kind == MediaKind.Video ? "video/mp4" : "image/jpeg",
            ContentHash = Guid.NewGuid().ToString("N"),
            ByteSize = 1,
            DurationMs = kind == MediaKind.Video ? 3000L : null,
            CapturedAt = capturedAt,
            RecordedAt = recordedAt,
            Classification = ClassificationLevels.ForOfficialUse,
            DivisionId = divisionId,
            IndexStatus = MediaIndexStatus.Indexed,
        };
        db.Assets.Add(asset);
        await db.SaveChangesAsync();

        var ids = new List<int>();
        for (var i = 0; i < faces.Length; i++)
        {
            MediaFrame? frame = null;
            if (kind == MediaKind.Video)
            {
                frame = new MediaFrame { AssetId = asset.Id, Index = i, TimestampMs = faces[i].TimestampMs };
                db.Frames.Add(frame);
                await db.SaveChangesAsync();
            }

            var face = new Face
            {
                AssetId = asset.Id,
                FrameId = frame?.Id,
                BoxX = 1, BoxY = 1, BoxWidth = 40, BoxHeight = 40,
                Landmarks = new float[10],
                DetectionScore = 0.9f,
                QualityScore = 0.8f,
                QualityAcceptable = true,
                CropStoredFileName = Guid.NewGuid().ToString("N") + ".jpg",
                TrackId = faces[i].TrackId,
                Classification = ClassificationLevels.ForOfficialUse,
                DivisionId = divisionId,
            };
            db.Faces.Add(face);
            await db.SaveChangesAsync();
            ids.Add(face.Id);
        }

        return (asset.Id, ids);
    }

    /// <summary>
    /// Сессия поиска по лицу-пробе с кандидатами (статус «кандидат»). Время создания задаётся явно — от него зависит
    /// попадание в период справки по делу. <paramref name="suggestedPerson"/> — предложение системы (ТФ-ПЕР-09).
    /// </summary>
    private static async Task<(int Id, IReadOnlyList<int> Candidates)> SeedSessionAsync(
        MediaDbContext db, int caseId, DateTime createdAtUtc, int probeFaceId, int? requestedBy, int? suggestedPerson,
        SearchScopeKind scope, int[] caseIds, int divisionId, params (int FaceId, int AssetId)[] candidates)
    {
        var session = new SearchSession
        {
            CaseId = caseId,
            AuthorizationRef = "Поручение № 7",
            Scope = scope,
            CaseIds = caseIds,
            ProbeSha256 = Guid.NewGuid().ToString("N"),
            ProbeFaceId = probeFaceId,
            TopK = 20,
            DetectorVersion = "yunet-test",
            EmbedderVersion = "sface-test",
            HnswEfSearch = 200,
            Classification = ClassificationLevels.ForOfficialUse,
            DivisionId = divisionId,
            RequestedByUserId = requestedBy,
            Origin = suggestedPerson is null ? SessionOrigin.Operator : SessionOrigin.SystemSuggestion,
            SuggestedPersonRef = suggestedPerson,
            CreatedAt = createdAtUtc,
        };
        db.SearchSessions.Add(session);
        await db.SaveChangesAsync();

        var ids = new List<int>();
        for (var i = 0; i < candidates.Length; i++)
        {
            var candidate = new SearchCandidate
            {
                SessionId = session.Id,
                Rank = i + 1,
                FaceId = candidates[i].FaceId,
                AssetId = candidates[i].AssetId,
                CosineDistance = 0.1 * (i + 1),
                ModelVersion = "sface-test",
                Classification = ClassificationLevels.ForOfficialUse,
                DivisionId = divisionId,
            };
            db.SearchCandidates.Add(candidate);
            await db.SaveChangesAsync();
            ids.Add(candidate.Id);
        }

        return (session.Id, ids);
    }

    // ---------- гриф строк (правка напрямую: предмет теста — сборщик, а не ведение грифов) ----------

    private Func<short, Task> PersonLevel(int id) => level => InInvestigationAsync(db =>
        db.Persons.Where(p => p.Id == id).ExecuteUpdateAsync(s => s.SetProperty(p => p.Classification, level)));

    private Func<short, Task> AppearanceLevel(int id) => level => InInvestigationAsync(db =>
        db.Appearances.Where(a => a.Id == id).ExecuteUpdateAsync(s => s.SetProperty(a => a.Classification, level)));

    private Func<short, Task> ReportLevel(int id) => level => InInvestigationAsync(db =>
        db.CaseReports.Where(r => r.Id == id).ExecuteUpdateAsync(s => s.SetProperty(r => r.Classification, level)));

    private Func<short, Task> AddressLevel(int id) => level => InInvestigationAsync(db =>
        db.PersonAddresses.Where(a => a.Id == id).ExecuteUpdateAsync(s => s.SetProperty(a => a.Classification, level)));

    private Func<short, Task> VehicleLevel(int id) => level => InInvestigationAsync(db =>
        db.PersonVehicles.Where(v => v.Id == id).ExecuteUpdateAsync(s => s.SetProperty(v => v.Classification, level)));

    private Func<short, Task> CaseLevel(int id) => level => InInvestigationAsync(db =>
        db.Cases.Where(c => c.Id == id).ExecuteUpdateAsync(s => s.SetProperty(c => c.Classification, level)));

    private Func<short, Task> AssetLevel(int id) => async level =>
    {
        await using var db = _media.CreateDbContext();
        (await db.Assets.Where(a => a.Id == id).ExecuteUpdateAsync(s => s.SetProperty(a => a.Classification, level))).ShouldBe(1);
    };

    private Func<short, Task> SessionLevel(int id) => async level =>
    {
        await using var db = _media.CreateDbContext();
        (await db.SearchSessions.Where(x => x.Id == id).ExecuteUpdateAsync(s => s.SetProperty(x => x.Classification, level))).ShouldBe(1);
    };

    private Func<short, Task> CandidateLevel(int id) => async level =>
    {
        await using var db = _media.CreateDbContext();
        (await db.SearchCandidates.Where(c => c.Id == id).ExecuteUpdateAsync(s => s.SetProperty(c => c.Classification, level))).ShouldBe(1);
    };

    private Func<short, Task> DocumentLevel(int id) => async level =>
    {
        await using var db = _docflow.CreateDbContext();
        (await db.Documents.Where(d => d.Id == id).ExecuteUpdateAsync(s => s.SetProperty(d => d.Classification, level))).ShouldBe(1);
    };

    // Правится ровно одна строка: иначе проверка «гриф не поднялся» прошла бы впустую на промахнувшемся идентификаторе.
    private async Task InInvestigationAsync(Func<InvestigationDbContext, Task<int>> update)
    {
        await using var db = _investigation.CreateDbContext();
        (await update(db)).ShouldBe(1);
    }

    /// <summary>Каждое сведение по очереди: поднято до «совершенно секретно» — гриф справки тот же; возвращено — снова ДСП.</summary>
    private static async Task ExpectRaisesAsync(Func<Task<short>> grade, string brief, IReadOnlyList<(string What, Func<short, Task> Set)> facts)
    {
        foreach (var (what, set) in facts)
        {
            await set(ClassificationLevels.TopSecret);
            (await grade()).ShouldBe(ClassificationLevels.TopSecret, $"{brief}: {what} должно поднимать гриф");
            await set(ClassificationLevels.ForOfficialUse);
            (await grade()).ShouldBe(ClassificationLevels.ForOfficialUse, $"{brief}: {what} — после возврата грифа");
        }
    }

    /// <summary>Не вошедшее в справку: поднято до «особой важности» (в пределах допуска) — гриф справки остаётся ДСП.</summary>
    private static async Task ExpectIgnoredAsync(Func<Task<short>> grade, string brief, IReadOnlyList<(string What, Func<short, Task> Set)> facts)
    {
        foreach (var (what, set) in facts)
        {
            await set(ClassificationLevels.SpecialImportance);
            (await grade()).ShouldBe(ClassificationLevels.ForOfficialUse, $"{brief}: {what} не должно поднимать гриф");
            await set(ClassificationLevels.ForOfficialUse);
        }
    }

    // ---------- чтение справки ----------

    private static BriefSection Section(Brief brief, string title) => brief.Sections.Single(s => s.Title == title);

    private static string FactValue(BriefSection section, string label) => section.Facts.Single(f => f.Label == label).Value;

    private static List<string> FactLines(BriefSection section) => section.Facts.Select(f => f.Label + ": " + f.Value).ToList();

    private static List<string> Lines(BriefSection section) =>
        section.Table.ShouldNotBeNull().Rows.Select(r => string.Join(" | ", r)).ToList();

    private static List<string> ReportDates(Brief brief) =>
        Section(brief, "Сводки и справки по бланку за период").Table.ShouldNotBeNull().Rows.Select(r => r[0]).ToList();

    private static DateTime Utc(int year, int month, int day, int hour) => new(year, month, day, hour, 0, 0, DateTimeKind.Utc);

    /// <summary>Время так, как его показывает справка: по часам сервера (как в карточках).</summary>
    private static string LocalText(DateTime utc) => utc.ToLocalTime().ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture);

    /// <summary>Засеянные идентификаторы «мира» дел.</summary>
    private sealed record World(
        AccessContext Admin,
        int DivisionId,
        int LeadId,
        int CaseA,
        int CaseB,
        int PersonP,
        int PersonU,
        int PersonL,
        int PersonQ,
        int PersonR,
        int AddressP,
        int VehicleP,
        int Video,
        int Image,
        int DocumentId,
        int SessionS1,
        int SessionOld,
        int SessionOther,
        IReadOnlyList<int> CandidatesS1,
        int AppearanceA1,
        int AppearanceA2,
        int ReportInPeriod,
        int ReportBeforePeriod,
        int ReportInactive);

    /// <summary>Часы сборщика: «сейчас» зафиксировано — период по умолчанию и время сбора не зависят от даты прогона.</summary>
    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class NoQueue : IBackgroundTaskQueue
    {
        public ValueTask<Guid> EnqueueAsync(
            string kind, Func<IServiceProvider, CancellationToken, Task> work, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Guid.NewGuid());
    }
}
