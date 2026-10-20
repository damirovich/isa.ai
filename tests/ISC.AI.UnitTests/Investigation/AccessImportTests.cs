using System.Runtime.CompilerServices;
using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.BackgroundTasks;
using ISC.AI.Abstractions.Security;
using ISC.AI.Abstractions.Storage;
using ISC.AI.Modules.Media.Application;
using ISC.AI.Modules.Media.Domain.Model;
using ISC.AI.Modules.Media.Domain.Services;
using ISC.AI.Profile.Investigation.Application.Features.AccessImport;
using ISC.AI.Profile.Investigation.Application.Features.Cases;
using ISC.AI.Profile.Investigation.Application.Features.Common;
using ISC.AI.Profile.Investigation.Domain.Enums;
using ISC.AI.Profile.Investigation.Domain.Services;
using NSubstitute;
using Shouldly;

namespace ISC.AI.UnitTests.Investigation;

/// <summary>
/// Перенос базы Access (ADR-0043): предложение сопоставления по названиям форм, извлечение фото из поля OLE, разбор
/// записей в поля системы (год рождения, пол, пустые реквизиты, длинные значения), отказы по записям, возобновление и
/// остановка по допуску, охрана сценариев (право, отпечаток проверки, допуск, подтверждение грифа).
/// </summary>
public sealed class AccessImportTests
{
    private const string Tasks = "Задания";
    private const string Links = "Связи";

    private static readonly AccessContext Operator = new("10", MaxClassification: 3, AllowedDivisions: [5]);
    private static readonly AccessImportOptions Options = new(5, 2, CaseStatus.InProgress, CreateMissingReferences: true);
    private static readonly AccessDatabaseFile File = new("stored.accdb", "otdel.accdb", 1024, new string('A', 64));

    private readonly IAccessImportRecordStore _records = Substitute.For<IAccessImportRecordStore>();
    private readonly IReferenceStore _references = Substitute.For<IReferenceStore>();
    private readonly ICaseStore _cases = Substitute.For<ICaseStore>();
    private readonly IPersonStore _persons = Substitute.For<IPersonStore>();
    private readonly IPersonRequisiteStore _requisites = Substitute.For<IPersonRequisiteStore>();
    private readonly IAuditWriter _audit = Substitute.For<IAuditWriter>();
    private readonly List<CaseDraft> _caseDrafts = [];
    private readonly List<PersonDraft> _personDrafts = [];
    private readonly List<AccessImportRecord> _saved = [];
    private int _nextId = 100;

    public AccessImportTests()
    {
        _records.ListAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<(string Table, string Key), AccessImportRecord>());
        _records.SaveAsync(Arg.Do<AccessImportRecord>(_saved.Add), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        _references.ListAsync(Arg.Any<ReferenceKind?>(), Arg.Any<CancellationToken>()).Returns(
        [
            new ReferenceItemRow(1, ReferenceKind.InitiatorUnit, "ГУ-1", null, 0, true),
            new ReferenceItemRow(2, ReferenceKind.InitiatorUnit, "Старое ГУ", null, 0, false),
            new ReferenceItemRow(3, ReferenceKind.LinkType, "Брат", null, 0, true),
        ]);
        _references.CreateAsync(Arg.Any<ReferenceKind>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(_ => (ReferenceWriteResult.Ok, ++_nextId));
        _cases.CreateAsync(Arg.Do<CaseDraft>(_caseDrafts.Add), Arg.Any<AccessContext>(), Arg.Any<CancellationToken>())
            .Returns(_ => (CaseWriteResult.Ok, ++_nextId));
        _persons.CreateAsync(Arg.Do<PersonDraft>(_personDrafts.Add), Arg.Any<AccessContext>(), Arg.Any<CancellationToken>())
            .Returns(_ => (PersonWriteResult.Ok, ++_nextId));
        _requisites.SaveVehicleAsync(Arg.Any<int>(), Arg.Any<int?>(), Arg.Any<PersonVehicleDraft>(), Arg.Any<AccessContext>(), Arg.Any<CancellationToken>())
            .Returns((PersonWriteResult.Ok, 1));
        _requisites.SaveAddressAsync(Arg.Any<int>(), Arg.Any<int?>(), Arg.Any<PersonAddressDraft>(), Arg.Any<AccessContext>(), Arg.Any<CancellationToken>())
            .Returns((PersonWriteResult.Ok, 1));
    }

    // --- Сопоставление ---

    [Fact(DisplayName = "ADR-0043: таблицы и столбцы, названные как в формах отдела, сопоставляются сами")]
    public void Suggest_maps_department_forms()
    {
        var mapping = AccessAutoMapper.Suggest(DepartmentTables());

        mapping.TasksTable.ShouldBe(Tasks);
        mapping.LinksTable.ShouldBe(Links);
        mapping.TaskColumns.ShouldBe(new Dictionary<AccessField, string>
        {
            [AccessField.TaskKey] = "Код",
            [AccessField.TaskNumber] = "№ задания",
            [AccessField.TaskDate] = "Дата",
            [AccessField.LastName] = "Фамилия",
            [AccessField.FirstName] = "Имя",
            [AccessField.MiddleName] = "Отчество",
            [AccessField.BirthYear] = "Год рождения",
            [AccessField.Sex] = "Пол",
            [AccessField.BirthPlace] = "Место рождения",
            [AccessField.WorkPlace] = "Место работы",
            [AccessField.Residence] = "Место жительства",
            [AccessField.Photo] = "Фото",
            [AccessField.Alias] = "Кличка",
            [AccessField.Measure] = "Мероприятие",
            [AccessField.Initiator] = "Инициатор",
            [AccessField.Justification] = "Обоснование",
            [AccessField.Purpose] = "Цель мероприятия",
            [AccessField.ObjectInfo] = "Сведения об объекте",
            [AccessField.Result] = "Результат мероприятия",
        }, ignoreOrder: true);
        mapping.LinkColumns.ShouldBe(new Dictionary<AccessField, string>
        {
            [AccessField.LinkKey] = "Код",
            [AccessField.LinkTaskNumber] = "№ задания",
            [AccessField.LinkType] = "Связь объекта",
            [AccessField.LinkInfo] = "Сведения об объекте связи",
            [AccessField.LinkName] = "ФИО связи",
            [AccessField.CarMake] = "Марка авто",
            [AccessField.CarPlate] = "Гос. номер",
            [AccessField.CarOwner] = "Владелец",
            [AccessField.VisitAddresses] = "Адреса посещения",
        }, ignoreOrder: true);
    }

    [Fact(DisplayName = "ADR-0043: фото предлагается только из двоичного столбца, текстовые поля — только из текстовых")]
    public void Suggest_keeps_binary_for_photo_only()
    {
        var table = new AccessTable("Объекты",
        [
            new AccessColumn("ФИО", "текст", false),
            new AccessColumn("Фото", "текст", false),
            new AccessColumn("Снимок", "объект OLE", true),
        ]);

        var mapping = AccessAutoMapper.Suggest([table]);

        mapping.TaskColumns[AccessField.FullName].ShouldBe("ФИО");
        mapping.TaskColumns[AccessField.Photo].ShouldBe("Снимок");
        mapping.TaskColumns.Values.ShouldNotContain("Фото");
    }

    [Fact(DisplayName = "ADR-0043: без таблицы с № задания или ФИО сопоставление пустое — оператор выбирает сам")]
    public void Suggest_returns_empty_without_task_table()
    {
        var mapping = AccessAutoMapper.Suggest([new AccessTable("Справочник", [new AccessColumn("Наименование", "текст", false)])]);

        mapping.TasksTable.ShouldBeNull();
        mapping.TaskColumns.ShouldBeEmpty();
    }

    // --- Фото ---

    [Fact(DisplayName = "ADR-0043: JPEG внутри обёртки OLE извлекается без заголовка и хвоста обёртки")]
    public void Photo_jpeg_is_cut_out_of_ole_wrapper()
    {
        var photo = AccessPhotoExtractor.TryExtract(Wrap(Jpeg)).ShouldNotBeNull();

        photo.ContentType.ShouldBe("image/jpeg");
        photo.Extension.ShouldBe(".jpg");
        photo.Content.ShouldBe(Jpeg);
    }

    [Fact(DisplayName = "ADR-0043: PNG — до блока IEND; BMP — по длине из заголовка, случайное «BM» перед ним пропускается")]
    public void Photo_png_and_bmp_use_their_own_structure()
    {
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 0x49, 0x45, 0x4E, 0x44, 0xAE, 0x42, 0x60, 0x82];
        AccessPhotoExtractor.TryExtract(Wrap(png)).ShouldNotBeNull().Content.ShouldBe(png);

        var bmp = Bmp();
        var photo = AccessPhotoExtractor.TryExtract([.. "BMxx"u8, .. Wrap(bmp)]).ShouldNotBeNull();
        photo.ContentType.ShouldBe("image/bmp");
        photo.Content.ShouldBe(bmp);
    }

    [Fact(DisplayName = "ADR-0043: поле без распознаваемой картинки — нет фото (запись идёт в отчёт)")]
    public void Photo_garbage_is_not_recognized()
    {
        AccessPhotoExtractor.TryExtract([1, 2, 3, 4, 5, 6, 7, 8]).ShouldBeNull();
        AccessPhotoExtractor.TryExtract("BM не картинка"u8).ShouldBeNull();
    }

    // --- Разбор и проверка ---

    [Fact(DisplayName = "ADR-0043: проверка — счёт, ошибки по записям (нет ФИО, связь без задания); повтор № — обе записи, вторая с пометкой, связь с таким № — в отчёт; записи справочников к заведению")]
    public async Task Plan_counts_and_reports_problems()
    {
        await using var database = Database(
            TaskRow(1, "1/24", "Иванов", initiator: "ГУ-1"),
            TaskRow(2, "2/24", "Петров", initiator: "Новое ГУ"),
            TaskRow(3, "2/24", "Сидоров"),
            TaskRow(4, "4/24", null),
            TaskRow(5, "5/24", "Козлов"),
            LinkRow(1, "1/24", "брат"),
            LinkRow(2, "5/24", "сосед"),
            LinkRow(3, "9/24", "друг"),
            LinkRow(4, null, "дядя"),
            LinkRow(5, "2/24", "кум"));

        var plan = await Importer().PlanAsync(database, File, Mapping(), Options);

        plan.TaskCount.ShouldBe(5);
        plan.NewTaskCount.ShouldBe(4, "повтор № задания не отказ: вторая запись переносится с пометкой (решение заказчика 16.10.2026)");
        plan.LinkCount.ShouldBe(5);
        plan.NewLinkCount.ShouldBe(2);
        plan.InitiatorsToCreate.ShouldBe(["Новое ГУ"]);
        plan.LinkTypesToCreate.ShouldBe(["сосед"], "«брат» уже есть — регистр не различается");
        plan.Problems.ShouldContain(p => p.Table == Tasks && p.Key == "3" && p.Message.StartsWith("задание перенесётся, но", StringComparison.Ordinal)
            && p.Message.Contains("«2/24 (запись 3)»", StringComparison.Ordinal));
        plan.Problems.ShouldContain(p => p.Table == Tasks && p.Key == "4" && p.Message.Contains("нет ФИО", StringComparison.Ordinal));
        plan.Problems.ShouldContain(p => p.Table == Links && p.Key == "3" && p.Message.Contains("9/24", StringComparison.Ordinal));
        plan.Problems.ShouldContain(p => p.Table == Links && p.Key == "4" && p.Message.Contains("не указан № задания", StringComparison.Ordinal));
        plan.Problems.ShouldContain(p => p.Table == Links && p.Key == "5" && p.Message.Contains("у нескольких заданий", StringComparison.Ordinal));
        plan.Sample.Select(s => (s.ObjectName, s.CaseNumber)).ShouldBe(
            [("Иванов", "1/24"), ("Петров", "2/24"), ("Сидоров", "2/24 (запись 3)"), ("Козлов", "5/24")]);
        plan.Problems.Single(p => p.Table == Tasks && p.Key == "3").Warning.ShouldBeTrue("запись переносится — это предупреждение, не ошибка");
        (plan.ErrorCount, plan.WarningCount).ShouldBe((4, 1));
        await _cases.DidNotReceive().CreateAsync(Arg.Any<CaseDraft>(), Arg.Any<AccessContext>(), Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "ADR-0043 п. 5: без галочки неизвестный инициатор и тип связи — отказ по строке; выключенная запись — отказ всегда")]
    public async Task Plan_without_creating_references_refuses_rows()
    {
        await using var database = Database(
            TaskRow(1, "1/24", "Иванов", initiator: "Новое ГУ"),
            TaskRow(2, "2/24", "Петров", initiator: "старое  гу"),
            TaskRow(3, "3/24", "Сидоров", initiator: "ГУ-1"),
            LinkRow(1, "3/24", "сосед"),
            LinkRow(2, "1/24", "брат"));

        var plan = await Importer().PlanAsync(database, File, Mapping(), Options with { CreateMissingReferences = false });

        plan.NewTaskCount.ShouldBe(1);
        plan.NewLinkCount.ShouldBe(0);
        plan.InitiatorsToCreate.ShouldBeEmpty();
        plan.Problems.ShouldContain(p => p.Key == "1" && p.Message.Contains("нет в справочнике ГУ", StringComparison.Ordinal));
        plan.Problems.ShouldContain(p => p.Key == "2" && p.Message.Contains("выключен", StringComparison.Ordinal));
        plan.Problems.ShouldContain(p => p.Table == Links && p.Key == "1" && p.Message.Contains("сосед", StringComparison.Ordinal));
        plan.Problems.ShouldContain(p => p.Table == Links && p.Key == "2" && p.Message.Contains("не переносится — связь тоже", StringComparison.Ordinal));
    }

    [Fact(DisplayName = "ADR-0043: перенос — дело-задание с реквизитами, объект с анкетой, связь с автомобилем и адресами посещения")]
    public async Task Run_builds_drafts_from_rows()
    {
        var longResidence = new string('ж', 1200);
        await using var database = Database(
            TaskRow(1, "1/24", "Иванов", firstName: "Иван", birth: "1975 г.р.", sex: "Жен.", residence: longResidence,
                initiator: null, justification: null, purpose: "Установить связи", measure: "НН", date: new DateTime(2024, 1, 2)),
            TaskRow(2, "2/24", null, fullName: "Петров Пётр", birth: new DateTime(1980, 5, 6), sex: "?", initiator: "гу-1"),
            LinkRow(1, "1/24", "брат", plate: "01 KG 123", make: "Toyota", owner: "отец", addresses: "ул. Ленина, 1\nмкр. Джал;  ;"),
            LinkRow(2, "2/24", null, name: "Сосед Сосед"));

        var report = await Importer().RunAsync(database, File, Mapping(), Options, Operator, 10);

        report.CasesCreated.ShouldBe(2);
        report.ObjectsCreated.ShouldBe(2);
        report.LinksCreated.ShouldBe(2);
        report.ReferencesCreated.ShouldBe(1, "только «Не указан (перенос из Access)»: «гу-1» совпал с «ГУ-1»");
        report.AddressesSaved.ShouldBe(2);
        report.VehiclesSaved.ShouldBe(1);
        report.Problems.ShouldBeEmpty();

        var task = _caseDrafts[0];
        task.Kind.ShouldBe(CaseKind.ObjectTask);
        task.Number.ShouldBe("1/24");
        task.Title.ShouldBe("Задание № 1/24 — Иванов Иван");
        task.OpenedAt.ShouldBe(new DateOnly(2024, 1, 2));
        task.DivisionId.ShouldBe(5);
        task.Classification.ShouldBe((short)2);
        task.CreatedByUserId.ShouldBe(10);
        var requisites = task.TaskRequisites.ShouldNotBeNull();
        requisites.TaskNumber.ShouldBe("1/24");
        requisites.InitiatorUnitId.ShouldNotBe(1);
        requisites.Justification.ShouldBe(AccessImporter.NotSpecified);
        requisites.Purpose.ShouldBe("Установить связи");
        requisites.Notes.ShouldNotBeNull().ShouldStartWith("Перенесено из базы Access «otdel.accdb»");
        requisites.Notes.ShouldContain("Мероприятие: НН");
        requisites.Notes.ShouldContain("otdel.accdb");
        _caseDrafts[1].TaskRequisites.ShouldNotBeNull().InitiatorUnitId.ShouldBe(1);
        _caseDrafts[1].TaskRequisites!.Notes.ShouldNotBeNull().ShouldContain("Даты задания в базе нет — дело открыто датой переноса");
        requisites.Notes.ShouldNotContain("Даты задания в базе нет");

        var target = _personDrafts.Single(p => p.CaseId == _saved.First(r => r.SourceKey == "1").CaseId && p.Role == PersonRole.Target);
        target.DisplayName.ShouldBe("Иванов Иван");
        var questionnaire = target.Questionnaire.ShouldNotBeNull();
        questionnaire.BirthYear.ShouldBe(1975);
        questionnaire.Sex.ShouldBe(PersonSex.Female);
        questionnaire.Residence.ShouldNotBeNull().Length.ShouldBe(1000);
        target.Notes.ShouldNotBeNull().ShouldContain("Место жительства (полностью): " + longResidence);

        var second = _personDrafts.Single(p => p.DisplayName == "Петров Пётр");
        second.Questionnaire.ShouldNotBeNull().BirthDate.ShouldBe(new DateOnly(1980, 5, 6));
        second.Questionnaire.Sex.ShouldBeNull();
        second.Notes.ShouldNotBeNull().ShouldContain("Пол в базе: «?»");

        var brother = _personDrafts.Single(p => p.Role == PersonRole.Link && p.LinkTypeId == 3);
        brother.IsUnidentified.ShouldBeTrue();
        brother.LinkedToPersonId.ShouldNotBeNull();
        var neighbour = _personDrafts.Single(p => p.DisplayName == "Сосед Сосед");
        neighbour.Role.ShouldBe(PersonRole.Link);
        neighbour.LinkTypeId.ShouldBeNull();

        await _requisites.Received(1).SaveVehicleAsync(Arg.Any<int>(), Arg.Is<int?>(v => v == null),
            new PersonVehicleDraft("01 KG 123", "Toyota", null, null, "Владелец: отец"), Operator, Arg.Any<CancellationToken>());
        await _requisites.Received(1).SaveAddressAsync(Arg.Any<int>(), Arg.Is<int?>(v => v == null),
            new PersonAddressDraft(AddressKind.Other, "ул. Ленина, 1", AccessImporter.VisitAddressNote), Operator, Arg.Any<CancellationToken>());
        await _requisites.Received(1).SaveAddressAsync(Arg.Any<int>(), Arg.Is<int?>(v => v == null),
            new PersonAddressDraft(AddressKind.Other, "мкр. Джал", AccessImporter.VisitAddressNote), Operator, Arg.Any<CancellationToken>());

        // Соответствия: по заданию — сначала дело, затем дело + объект; по связи — фигурант.
        _saved.Count(r => r.SourceTable == Tasks).ShouldBe(4);
        _saved.Where(r => r.SourceTable == Links).ShouldAllBe(r => r.PersonId != null);
        await _audit.Received(2).WriteAsync(Arg.Is<AuditEntry>(e => e.Action == AuditAction.Ingest), Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "ADR-0043: примечание длиннее поля сохраняется обрезанным, и это видно в проверке и в отчёте — не молча")]
    public async Task Truncated_notes_are_reported()
    {
        var info = new string('с', 4500);
        await using var database = Database(TaskRow(1, "1/24", "Иванов", info: info));

        var plan = await Importer().PlanAsync(database, File, Mapping(), Options);
        var report = await Importer().RunAsync(database, File, Mapping(), Options, Operator, 10);

        plan.NewTaskCount.ShouldBe(1);
        plan.Problems.ShouldHaveSingleItem().Message.ShouldStartWith("задание перенесётся, но примечание объекта длиннее 4000 знаков");
        report.CasesCreated.ShouldBe(1);
        report.Problems.ShouldHaveSingleItem().Message.ShouldStartWith("задание перенесено, но примечание объекта длиннее 4000 знаков");
        _personDrafts.ShouldHaveSingleItem().Notes.ShouldNotBeNull().Length.ShouldBe(4000);
    }

    [Fact(DisplayName = "ADR-0043 п. 6: повтор после сбоя — дело не заводится заново, объект берётся из дела, перенесённые связи пропускаются")]
    public async Task Run_resumes_from_records()
    {
        _records.ListAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new Dictionary<(string Table, string Key), AccessImportRecord>
        {
            [(Tasks, "1")] = new(File.Sha256, Tasks, "1", 50, null),
            [(Tasks, "2")] = new(File.Sha256, Tasks, "2", 60, 61),
            [(Links, "1")] = new(File.Sha256, Links, "1", 50, 70),
        });
        _persons.ListByCaseAsync(50, Operator, Arg.Any<CancellationToken>()).Returns(
            [new PersonRow(51, 50, "Иванов", false, null, null, null, 2, 5, 0, 0, PersonRole.Target)]);
        await using var database = Database(
            TaskRow(1, "1/24", "Иванов"),
            TaskRow(2, "2/24", "Петров"),
            LinkRow(1, "1/24", "брат"),
            LinkRow(2, "2/24", "брат"));

        var report = await Importer().RunAsync(database, File, Mapping(), Options, Operator, 10);

        report.CasesCreated.ShouldBe(0);
        report.ObjectsCreated.ShouldBe(0);
        report.AlreadyImported.ShouldBe(1);
        report.LinksCreated.ShouldBe(1);
        _personDrafts.ShouldHaveSingleItem().LinkedToPersonId.ShouldBe(61);
        _saved.ShouldContain(r => r.SourceKey == "1" && r.SourceTable == Tasks && r.PersonId == 51);
    }

    [Fact(DisplayName = "ADR-0043: занятый № дела — отказ записи и её связей (слияния нет); вне допуска — перенос останавливается")]
    public async Task Run_refuses_duplicates_and_stops_outside_clearance()
    {
        _cases.CreateAsync(Arg.Is<CaseDraft>(d => d.Number == "1/24"), Arg.Any<AccessContext>(), Arg.Any<CancellationToken>())
            .Returns((CaseWriteResult.DuplicateNumber, 0));
        _cases.CreateAsync(Arg.Is<CaseDraft>(d => d.Number == "2/24"), Arg.Any<AccessContext>(), Arg.Any<CancellationToken>())
            .Returns((CaseWriteResult.OutsideClearance, 0));
        await using var database = Database(
            TaskRow(1, "1/24", "Иванов", initiator: "ГУ-1"),
            TaskRow(2, "2/24", "Петров", initiator: "ГУ-1"),
            TaskRow(3, "3/24", "Сидоров", initiator: "ГУ-1"),
            LinkRow(1, "1/24", "брат"));

        var report = await Importer().RunAsync(database, File, Mapping(), Options, Operator, 10);

        report.Stopped.ShouldBeTrue();
        report.Problems[0].Message.ShouldBe(CaseGuard.OutsideClearance);
        report.Problems.ShouldContain(p => p.Key == "1" && p.Message.Contains("уже есть", StringComparison.Ordinal));
        report.Problems.ShouldContain(p => p.Table == Links && p.Key == "1");
        await _cases.DidNotReceive().CreateAsync(Arg.Is<CaseDraft>(d => d.Number == "3/24"), Arg.Any<AccessContext>(), Arg.Any<CancellationToken>());
        _personDrafts.ShouldBeEmpty();
    }

    [Fact(DisplayName = "ADR-0043: сопоставление без ФИО или со столбцом, которого нет, — отказ до чтения")]
    public async Task Invalid_mapping_is_refused()
    {
        await using var database = Database(TaskRow(1, "1/24", "Иванов"));
        var noName = Mapping() with
        {
            TaskColumns = new Dictionary<AccessField, string> { [AccessField.TaskNumber] = "№ задания" },
        };
        var missing = Mapping() with
        {
            TaskColumns = new Dictionary<AccessField, string> { [AccessField.LastName] = "Нет такого" },
        };

        (await Should.ThrowAsync<AccessDatabaseReadException>(() => Importer().PlanAsync(database, File, noName, Options)))
            .Message.ShouldContain("фамилии или ФИО");
        (await Should.ThrowAsync<AccessDatabaseReadException>(() => Importer().PlanAsync(database, File, missing, Options)))
            .Message.ShouldContain("Нет такого");
    }

    // --- Структура реальной базы отдела (16.10.2026, ADR-0043 v0.3) — значения вымышленные ---

    [Fact(DisplayName = "ADR-0043 v0.3: реальная база отдела — «задания», «связи» и «инициатор» сопоставляются сами, включая «место жительство», гражданство, кличку и фото связи")]
    public void Suggest_maps_real_department_base()
    {
        var mapping = AccessAutoMapper.Suggest(RealTables());

        mapping.TasksTable.ShouldBe(RealTasks);
        mapping.LinksTable.ShouldBe(RealLinks);
        mapping.InitiatorsTable.ShouldBe(RealInitiators);
        mapping.TaskColumns.ShouldBe(new Dictionary<AccessField, string>
        {
            [AccessField.TaskKey] = "Код №",
            [AccessField.TaskNumber] = "№ задания",
            [AccessField.LastName] = "Фамилия",
            [AccessField.FirstName] = "имя",
            [AccessField.MiddleName] = "отчество",
            [AccessField.BirthYear] = "год рождения",
            [AccessField.Sex] = "пол",
            [AccessField.Measure] = "мероприятие",
            [AccessField.WorkPlace] = "место работы",
            [AccessField.Residence] = "место жительство",
            [AccessField.Citizenship] = "гражданство",
            [AccessField.Justification] = "обоснование",
            [AccessField.Initiator] = "инициатор",
            [AccessField.BirthPlace] = "место рождения",
            [AccessField.ObjectInfo] = "сведения об объекте",
            [AccessField.Photo] = "фотография",
            [AccessField.Alias] = "кличка",
            [AccessField.Purpose] = "цель мероприятия",
            [AccessField.Result] = "результат мероприятия",
        }, ignoreOrder: true);
        mapping.LinkColumns.ShouldBe(new Dictionary<AccessField, string>
        {
            [AccessField.LinkKey] = "Код связи",
            [AccessField.LinkTaskNumber] = "№ задания",
            [AccessField.LinkAlias] = "Кличка объекта",
            [AccessField.LinkInfo] = "Сведения об объекте связи",
            [AccessField.CarMake] = "Марка авто",
            [AccessField.CarPlate] = "Гос номер авто",
            [AccessField.CarOwner] = "Владелец авто",
            [AccessField.VisitAddresses] = "Адреса посещения связи",
            [AccessField.LinkPhoto] = "Фото",
        }, ignoreOrder: true);
        mapping.InitiatorColumns.ShouldBe(new Dictionary<AccessField, string>
        {
            [AccessField.InitiatorTaskNumber] = "№ задания",
            [AccessField.InitiatorUnit] = "инициатор задания",
            [AccessField.InitiatorHead] = "нач управления",
            [AccessField.InitiatorExecutor] = "исполнитель",
            [AccessField.InitiatorDepartment] = "отдел",
            [AccessField.InitiatorPhone] = "телефон",
        }, ignoreOrder: true);
    }

    [Fact(DisplayName = "ADR-0043 v0.3: фото «Paint.Picture» (как в реальной базе) — BMP по длине из заголовка, без обёртки OLE и хвоста")]
    public void Photo_paint_picture_bmp_is_extracted()
    {
        var bmp = Bmp();

        var photo = AccessPhotoExtractor.TryExtract(PaintPicture(bmp)).ShouldNotBeNull();

        photo.ContentType.ShouldBe("image/bmp");
        photo.Content.ShouldBe(bmp);
    }

    [Fact(DisplayName = "ADR-0043 v0.3: «6ГУ», «6Гу» и «6-ГУ» — та же запись справочника «6 ГУ», новая не заводится; другое ГУ — заводится")]
    public async Task Initiator_names_match_ignoring_spaces_and_signs()
    {
        UseRealReferences();
        await using var database = new FakeAccessDatabase(RealTables(),
        [
            RealTask(1, "1/", "Первов", initiator: "6ГУ"),
            RealTask(2, "2/", "Второв", initiator: "6Гу"),
            RealTask(3, "3/", "Третьев", initiator: "6-ГУ"),
            RealTask(4, "4/", "Четвёртов", initiator: "7 ГУ"),
        ]);

        var plan = await Importer().PlanAsync(database, File, AccessAutoMapper.Suggest(RealTables()), Options);

        plan.NewTaskCount.ShouldBe(4);
        plan.InitiatorsToCreate.ShouldBe(["7 ГУ"]);
    }

    [Fact(DisplayName = "ADR-0043 v0.3: проверка реальной базы — пустые строки не ошибки, повтор № с пометкой, связь с кличкой и фото, сведения инициатора, «6ГУ» = «6 ГУ»")]
    public async Task Plan_real_department_base()
    {
        UseRealReferences();
        await using var database = RealDatabase();

        var plan = await Importer().PlanAsync(database, File, AccessAutoMapper.Suggest(RealTables()), Options);

        plan.TaskCount.ShouldBe(4, "пустая строка задания не считается заданием");
        plan.NewTaskCount.ShouldBe(4);
        plan.PhotoCount.ShouldBe(1);
        plan.InitiatorsApplied.ShouldBe(1);
        plan.LinkCount.ShouldBe(4, "две пустые строки — не связи (строка с одним фото — связь с ошибкой)");
        plan.NewLinkCount.ShouldBe(1);
        plan.LinkPhotoCount.ShouldBe(1);
        plan.EmptyRows.ShouldBe(4, "задание, две связи и строка инициатора с одним № задания");
        plan.InitiatorsToCreate.ShouldBeEmpty("«6ГУ», «6Гу» — та же запись «6 ГУ»");
        plan.Problems.Select(p => (p.Table, p.Key)).ShouldBe(
        [
            (RealLinks, "3"), (RealLinks, "5"), (RealLinks, "6"), (RealInitiators, "1/"), (RealTasks, "3"),
        ], ignoreOrder: true);
        plan.Problems.Single(p => p.Table == RealLinks && p.Key == "3").Message.ShouldContain("не указан № задания");
        plan.Problems.Single(p => p.Table == RealLinks && p.Key == "5").Message.ShouldContain("у нескольких заданий");
        plan.Problems.Single(p => p.Table == RealLinks && p.Key == "6").Message.ShouldBe(AccessImporter.OnlyPhotoLinkMessage);
        plan.Problems.Single(p => p.Table == RealInitiators).Message.ShouldContain("у нескольких заданий");
        plan.Problems.Single(p => p.Table == RealTasks).Message.ShouldContain("«1/ (запись 3)»");
    }

    [Fact(DisplayName = "ADR-0043 v0.3, ADR-0055: перенос реальной базы — № дела с пометкой при повторе, гражданство и сведения об объекте связи в анкете, реквизиты инициатора из таблицы, связь с кличкой, автомобилем, адресами и фото")]
    public async Task Run_real_department_base()
    {
        UseRealReferences();
        await using var database = RealDatabase();

        var report = await Importer().RunAsync(database, File, AccessAutoMapper.Suggest(RealTables()), Options, Operator, 10);

        report.CasesCreated.ShouldBe(4);
        report.ObjectsCreated.ShouldBe(4);
        report.LinksCreated.ShouldBe(1);
        report.VehiclesSaved.ShouldBe(1);
        report.AddressesSaved.ShouldBe(2);
        report.ReferencesCreated.ShouldBe(0);
        report.EmptyRows.ShouldBe(4);
        _caseDrafts.Select(d => d.Number).ShouldBe(["1/", "ACCESS-2", "1/ (запись 3)", "5/"]);
        _caseDrafts.ShouldAllBe(d => d.TaskRequisites!.InitiatorUnitId == 4);
        _caseDrafts[2].TaskRequisites!.TaskNumber.ShouldBe("1/", "реквизит задания — № как в базе, пометка только в № дела");
        _caseDrafts[2].TaskRequisites!.Notes.ShouldNotBeNull().ShouldContain("повторяется (также у записи 1)");
        _caseDrafts[0].TaskRequisites!.Notes.ShouldNotBeNull().ShouldContain("Даты задания в базе нет");

        var withInitiator = _caseDrafts[3].TaskRequisites.ShouldNotBeNull();
        withInitiator.InitiatorName.ShouldBe("Исполнитель И.И.");
        withInitiator.InitiatorPhone.ShouldBe("12-34-56");
        withInitiator.InitiatorDetails.ShouldBe("Начальник управления: Начальник Н.Н.; Отдел: 2 отдел");
        _caseDrafts[0].TaskRequisites!.InitiatorName.ShouldBeNull();

        var first = _personDrafts.First(p => p.Role == PersonRole.Target);
        first.Questionnaire.ShouldNotBeNull().Residence.ShouldBe("г. Город, ул. Первая, 1");
        first.Questionnaire.Sex.ShouldBe(PersonSex.Male);
        first.Questionnaire.BirthYear.ShouldBe(1985);
        // ADR-0055: гражданство — в поле анкеты, а не в примечание.
        var second = _personDrafts.Single(p => p.DisplayName == "Второв Второй");
        second.Questionnaire.ShouldNotBeNull().Citizenship.ShouldBe("Кыргызстан");
        (second.Notes ?? string.Empty).ShouldNotContain("Гражданство:");

        var link = _personDrafts.Single(p => p.Role == PersonRole.Link);
        link.IsUnidentified.ShouldBeTrue();
        link.Questionnaire.ShouldNotBeNull().Alias.ShouldBe("Лис");
        link.Questionnaire.Orientation.ShouldBe("встречается с объектом"); // «сведения об объекте связи» — в ориентировку
        (link.Notes ?? string.Empty).ShouldNotContain("Сведения об объекте связи:");

        // Приём фото здесь подменён (дело «недоступно») — видно, что фото объекта и фото связи дошли до приёма и
        // отказ не молчаливый.
        report.Problems.ShouldContain(p => p.Table == RealTasks && p.Key == "1" && p.Message.Contains("фото не принято", StringComparison.Ordinal));
        report.Problems.ShouldContain(p => p.Table == RealLinks && p.Key == "4" && p.Message.StartsWith("связь перенесена, но фото", StringComparison.Ordinal));
        report.Problems.ShouldContain(p => p.Table == RealLinks && p.Key == "6" && p.Message == AccessImporter.OnlyPhotoLinkMessage);
    }

    [Fact(DisplayName = "ADR-0043 v0.3: инициатор в задании и в таблице расходятся — берётся из таблицы, расхождение в примечании; строка без № и строка-повтор — в отчёт")]
    public async Task Initiator_table_wins_and_reports_unapplied_rows()
    {
        UseRealReferences();
        await using var database = new FakeAccessDatabase(RealTables(),
        [
            RealTask(1, "7/", "Седьмов", initiator: "ГУ-1"),
            RealInitiator("7/", unit: "6 ГУ", executor: "Исполнитель И.И."),
            RealInitiator("7/", executor: "Другой Д.Д."),
            RealInitiator(null, executor: "Без номера"),
        ]);

        var report = await Importer().RunAsync(database, File, AccessAutoMapper.Suggest(RealTables()), Options, Operator, 10);

        var requisites = _caseDrafts.ShouldHaveSingleItem().TaskRequisites.ShouldNotBeNull();
        requisites.InitiatorUnitId.ShouldBe(4);
        requisites.InitiatorName.ShouldBe("Исполнитель И.И.");
        requisites.Notes.ShouldNotBeNull().ShouldContain("Инициатор в задании: «ГУ-1»; в таблице инициаторов: «6 ГУ» — взят из таблицы.");
        report.Problems.ShouldContain(p => p.Table == RealInitiators && p.Key == "7/" && p.Message.Contains("взята первая строка", StringComparison.Ordinal));
        report.Problems.ShouldContain(p => p.Table == RealInitiators && p.Key == "#3" && p.Message.Contains("не указан № задания", StringComparison.Ordinal));
    }

    [Fact(DisplayName = "ADR-0043 v0.3: инициатор — только по № задания (не по коду записи); ссылка на отказное задание — в отчёт, а не к чужому заданию с таким же кодом")]
    public async Task Initiators_and_links_do_not_fall_through_to_other_tasks()
    {
        UseRealReferences();
        await using var database = new FakeAccessDatabase(RealTables(),
        [
            RealTask(3, "10/", "Третьев", initiator: "6ГУ"),
            RealTask(5, "3", null, initiator: "6ГУ"),
            RealTask(7, "40/", "Седьмов", initiator: "6ГУ"),
            RealInitiator("7", executor: "Чужой Ч.Ч."),
            RealInitiator("3", executor: "Отказной О.О."),
            RealLink(1, "3", "Лис"),
            RealLink(2, "7", "Волк"),
        ]);

        var report = await Importer().RunAsync(database, File, AccessAutoMapper.Suggest(RealTables()), Options, Operator, 10);

        report.CasesCreated.ShouldBe(2);
        _caseDrafts.ShouldAllBe(d => d.TaskRequisites!.InitiatorName == null, "строки «7» и «3» не легли ни в «10/», ни в «40/»");
        report.Problems.ShouldContain(p => p.Table == RealInitiators && p.Key == "7" && p.Message.Contains("не найдено", StringComparison.Ordinal));
        report.Problems.ShouldContain(p => p.Table == RealInitiators && p.Key == "3" && p.Message.Contains("не переносится", StringComparison.Ordinal));
        report.Problems.ShouldContain(p => p.Table == RealLinks && p.Key == "1" && p.Message == "задание «3» не переносится — связь тоже");

        // Связь с № «7», которого нет ни у одного задания, — по коду записи 7 (так в базах бывает), к «40/».
        var link = _personDrafts.Single(p => p.Role == PersonRole.Link);
        link.Questionnaire.ShouldNotBeNull().Alias.ShouldBe("Волк");
        link.CaseId.ShouldBe(_saved.Single(r => r.SourceTable == RealTasks && r.SourceKey == "7" && r.PersonId != null).CaseId);
    }

    [Fact(DisplayName = "ADR-0043 v0.3: повтор № с отказной записью — № всё равно неоднозначен: переносимое задание сохраняет №, связь с этим № — в отчёт")]
    public async Task Duplicate_number_with_rejected_row_is_ambiguous()
    {
        UseRealReferences();
        await using var database = new FakeAccessDatabase(RealTables(),
        [
            RealTask(1, "1/", null, initiator: "6ГУ"),
            RealTask(3, "1/", "Третьев", initiator: "6ГУ"),
            RealLink(1, "1/", "Лис"),
        ]);

        var plan = await Importer().PlanAsync(database, File, AccessAutoMapper.Suggest(RealTables()), Options);

        plan.NewTaskCount.ShouldBe(1);
        plan.Sample.ShouldHaveSingleItem().CaseNumber.ShouldBe("1/");
        plan.NewLinkCount.ShouldBe(0);
        plan.Problems.ShouldContain(p => p.Table == RealLinks && p.Message.Contains("у нескольких заданий", StringComparison.Ordinal));
    }

    [Fact(DisplayName = "ADR-0043 v0.3: без столбца кода записи повтор № не считается повтором строки — переносятся обе, вторая «№ (строка N)»")]
    public async Task Without_key_column_duplicate_numbers_import_both()
    {
        UseRealReferences();
        var suggested = AccessAutoMapper.Suggest(RealTables());
        var mapping = suggested with
        {
            TaskColumns = suggested.TaskColumns.Where(c => c.Key != AccessField.TaskKey).ToDictionary(c => c.Key, c => c.Value),
        };
        await using var database = new FakeAccessDatabase(RealTables(),
        [
            RealTask(1, "1/", "Первов", initiator: "6ГУ"),
            RealTask(2, "1/", "Второв", initiator: "6ГУ"),
        ]);

        var report = await Importer().RunAsync(database, File, mapping, Options, Operator, 10);

        report.CasesCreated.ShouldBe(2);
        _caseDrafts.Select(d => d.Number).ShouldBe(["1/", "1/ (строка 2)"]);
        report.Problems.ShouldHaveSingleItem().Warning.ShouldBeTrue();
        _saved.Where(r => r.SourceTable == RealTasks).Select(r => r.SourceKey).Distinct().ShouldBe(["1/", "1/#2"], ignoreOrder: true);
    }

    [Fact(DisplayName = "ADR-0043 v0.3: связь «№ задания + фото» — неустановленное лицо с фото, а не «сведений нет»; повтор кода связи — в отчёт")]
    public async Task Link_with_photo_only_is_imported_and_duplicate_link_key_is_reported()
    {
        UseRealReferences();
        await using var database = new FakeAccessDatabase(RealTables(),
        [
            RealTask(1, "1/", "Первов", initiator: "6ГУ"),
            RealLink(1, "1/", photo: Wrap(Jpeg)),
            RealLink(2, "1/", "Лис"),
            RealLink(2, "1/", "Волк"),
        ]);

        var plan = await Importer().PlanAsync(database, File, AccessAutoMapper.Suggest(RealTables()), Options);

        plan.NewLinkCount.ShouldBe(2);
        plan.LinkPhotoCount.ShouldBe(1);
        plan.Problems.ShouldContain(p => p.Table == RealLinks && p.Key == "2" && p.Message.Contains("повтор кода связи", StringComparison.Ordinal));

        var report = await ImporterWithMedia().RunAsync(database, File, AccessAutoMapper.Suggest(RealTables()), Options, Operator, 10);
        report.LinksCreated.ShouldBe(2);
        report.LinkPhotosAccepted.ShouldBe(1);
        _personDrafts.Count(p => p.Role == PersonRole.Link && p.IsUnidentified && p.Questionnaire is null).ShouldBe(1);
    }

    [Fact(DisplayName = "ADR-0043 v0.3: принятое фото связи отмечается; повтор не принимает его снова, а прерванный проход фото доводит")]
    public async Task Link_photo_marker_makes_repeat_finish_interrupted_photos()
    {
        UseRealReferences();
        var mapping = AccessAutoMapper.Suggest(RealTables());
        await using var database = new FakeAccessDatabase(RealTables(),
        [
            RealTask(1, "1/", "Первов", initiator: "6ГУ"),
            RealLink(4, "1/", "Лис", photo: Wrap(Jpeg)),
        ]);

        var first = await ImporterWithMedia().RunAsync(database, File, mapping, Options, Operator, 10);
        first.LinkPhotosAccepted.ShouldBe(1);
        var photoTable = AccessImporter.LinkPhotoTable(mapping);
        var marker = _saved.Where(r => r.SourceTable == photoTable).ShouldHaveSingleItem();
        marker.SourceKey.ShouldBe("4");
        marker.PersonId.ShouldNotBeNull();

        // Повтор после полного переноса: работы нет.
        var done = _saved.GroupBy(r => (r.SourceTable, r.SourceKey)).ToDictionary(g => g.Key, g => g.Last());
        _records.ListAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(done);
        var again = await ImporterWithMedia().PlanAsync(database, File, mapping, Options);
        again.LinkPhotoCount.ShouldBe(0);
        again.HasWork.ShouldBeFalse();

        // Повтор после сбоя на проходе фото (связь записана, отметки фото нет): фото доводится.
        done.Remove((photoTable, "4"));
        var resumed = await ImporterWithMedia().PlanAsync(database, File, mapping, Options);
        resumed.LinkPhotoCount.ShouldBe(1);
        resumed.HasWork.ShouldBeTrue();
        var report = await ImporterWithMedia().RunAsync(database, File, mapping, Options, Operator, 10);
        report.LinksCreated.ShouldBe(0);
        report.LinkPhotosAccepted.ShouldBe(1);
    }

    [Fact(DisplayName = "ADR-0043 v0.3: инициатор длиннее записи справочника — полностью в примечании; примечание задания начинается с «откуда перенесено»")]
    public async Task Long_initiator_is_kept_in_notes()
    {
        UseRealReferences();
        var longInitiator = "6 ГУ " + new string('у', 400);
        await using var database = new FakeAccessDatabase(RealTables(), [RealTask(1, "1/", "Первов", initiator: longInitiator)]);

        var plan = await Importer().PlanAsync(database, File, AccessAutoMapper.Suggest(RealTables()), Options);
        await Importer().RunAsync(database, File, AccessAutoMapper.Suggest(RealTables()), Options, Operator, 10);

        plan.InitiatorsToCreate.ShouldHaveSingleItem().Length.ShouldBe(300);
        var notes = _caseDrafts.ShouldHaveSingleItem().TaskRequisites!.Notes.ShouldNotBeNull();
        notes.ShouldStartWith("Перенесено из базы Access «otdel.accdb», таблица «задания», код записи 1.");
        notes.ShouldContain("Инициатор (полностью): " + longInitiator);
    }

    // --- Охрана сценариев ---

    [Fact(DisplayName = "ADR-0043 п. 9: без права «Перенос баз Access» — отказ; база не открывается")]
    public async Task Handlers_require_import_permission()
    {
        var (roles, subjects, reader) = Guard(InvestigationRole.Investigator);
        var files = new AccessImportFiles(Substitute.For<IFileStorage>());
        var accessProvider = Substitute.For<IAccessContextProvider>();

        var check = await new CheckAccessImportQuery.Handler(roles, subjects, accessProvider, reader, files, Importer())
            .Handle(new CheckAccessImportQuery(File, Mapping(), Options with { CreateMissingReferences = false }), CancellationToken.None);

        check.Status.ShouldBeFalse();
        check.StatusMessage.ShouldBe(RoleGuard.Denied(InvestigationPermissions.CasesImport));
        await reader.DidNotReceive().OpenAsync(Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "ADR-0043: запуск с отпечатком не от этой проверки — конфликт; гриф вне допуска — отказ ТБ-024")]
    public async Task Run_requires_fresh_check_and_clearance()
    {
        var (roles, subjects, reader) = Guard(InvestigationRole.Administrator);
        var accessProvider = Substitute.For<IAccessContextProvider>();
        accessProvider.GetCurrentAsync(Arg.Any<CancellationToken>()).Returns(Operator);
        var handler = new RunAccessImportCommand.Handler(
            roles, subjects, accessProvider, reader, new AccessImportFiles(Substitute.For<IFileStorage>()), Importer());
        var options = Options with { CreateMissingReferences = false };
        var fingerprint = (await PlanForFingerprintAsync(options)).Fingerprint;

        var stale = await handler.Handle(
            new RunAccessImportCommand(File, Mapping(), options with { Status = CaseStatus.Suspended }, fingerprint, true), CancellationToken.None);
        stale.StatusCode.ShouldBe(ResponseStatusCode.Conflict);
        stale.StatusMessage.ShouldBe(RunAccessImportCommand.StaleCheck);

        var tooHigh = options with { Classification = 4 };
        var outside = await handler.Handle(
            new RunAccessImportCommand(File, Mapping(), tooHigh, (await PlanForFingerprintAsync(tooHigh)).Fingerprint, true), CancellationToken.None);
        outside.StatusMessage.ShouldBe(CaseGuard.OutsideClearance);
        await reader.DidNotReceive().OpenAsync(Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "ТБ-024: без подтверждения грифа и подразделения, со статусом «закрыто» или без подразделения перенос не запускается")]
    public void Run_validator_requires_confirmation()
    {
        var validator = new RunAccessImportValidator();

        validator.Validate(new RunAccessImportCommand(File, Mapping(), Options, "abc", ConfirmedClassification: false)).IsValid.ShouldBeFalse();
        validator.Validate(new RunAccessImportCommand(File, Mapping(), Options with { Status = CaseStatus.Closed }, "abc", true)).IsValid.ShouldBeFalse();
        validator.Validate(new RunAccessImportCommand(File, Mapping(), Options with { DivisionId = 0 }, "abc", true)).IsValid.ShouldBeFalse();
        validator.Validate(new RunAccessImportCommand(File, Mapping(), Options, "abc", true)).IsValid.ShouldBeTrue();
        new UploadAccessDatabaseValidator().Validate(new UploadAccessDatabaseCommand(Stream.Null, "база.xlsx", 10)).IsValid.ShouldBeFalse();
    }

    private async Task<AccessImportPlan> PlanForFingerprintAsync(AccessImportOptions options)
    {
        await using var database = Database(TaskRow(1, "1/24", "Иванов", initiator: "ГУ-1"));
        return await Importer().PlanAsync(database, File, Mapping(), options);
    }

    private static (IUserRoleStore Roles, ISubjectProvider Subjects, IAccessDatabaseReader Reader) Guard(InvestigationRole role)
    {
        var roles = Substitute.For<IUserRoleStore>();
        roles.GetRoleAsync(10, Arg.Any<CancellationToken>()).Returns(role);
        var subjects = Substitute.For<ISubjectProvider>();
        subjects.GetCurrentUserIdAsync(Arg.Any<CancellationToken>()).Returns((int?)10);
        var reader = Substitute.For<IAccessDatabaseReader>();
        reader.UnavailableReason.Returns((string?)null);
        return (roles, subjects, reader);
    }

    private AccessImporter Importer() => new(
        _records, _references, _cases, _persons, _requisites, Substitute.For<ICaseScope>(), Substitute.For<IMediaStore>(),
        MediaUploadOptions.Default, Substitute.For<IBackgroundTaskQueue>(), _audit, TimeProvider.System);

    // Импорт с приёмом фото, который принимает файл: дело доступно, хранилище дочитывает поток (как настоящее).
    private AccessImporter ImporterWithMedia()
    {
        var scope = Substitute.For<ICaseScope>();
        scope.GetCaseAsync(Arg.Any<int>(), Arg.Any<AccessContext>(), Arg.Any<CancellationToken>())
            .Returns(call => new CaseScopeItem(call.ArgAt<int>(0), "N", "Дело", 2, 5));
        var media = Substitute.For<IMediaStore>();
        media.ReceiveAsync(Arg.Any<MediaAssetDraft>(), Arg.Any<Stream>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                await call.Arg<Stream>().CopyToAsync(Stream.Null);
                return new MediaAssetReceipt(77, Duplicate: false);
            });
        return new(_records, _references, _cases, _persons, _requisites, scope, media, MediaUploadOptions.Default,
            Substitute.For<IBackgroundTaskQueue>(), _audit, TimeProvider.System);
    }

    private static AccessMapping Mapping()
    {
        // «ФИО одним полем» по названию не узнаётся (в формах его нет) — оператор выбирает его сам.
        var suggested = AccessAutoMapper.Suggest(DepartmentTables());
        return suggested with
        {
            TaskColumns = new Dictionary<AccessField, string>(suggested.TaskColumns) { [AccessField.FullName] = "ФИО одним полем" },
        };
    }

    private static List<AccessTable> DepartmentTables() =>
    [
        new(Tasks,
        [
            Text("Код"), Text("№ задания"), new("Дата", "дата", false), Text("Фамилия"), Text("Имя"), Text("Отчество"),
            Text("ФИО одним полем"), Text("Год рождения"), Text("Пол"), Text("Место рождения"), Text("Место работы"),
            Text("Место жительства"), new("Фото", "объект OLE", true), Text("Кличка"), Text("Мероприятие"), Text("Инициатор"),
            Text("Обоснование"), Text("Цель мероприятия"), Text("Сведения об объекте"), Text("Результат мероприятия"),
        ]),
        new(Links,
        [
            Text("Код"), Text("№ задания"), Text("Связь объекта"), Text("Сведения об объекте связи"), Text("ФИО связи"),
            Text("Марка авто"), Text("Гос. номер"), Text("Владелец"), Text("Адреса посещения"),
        ]),
        new("Инициаторы", [Text("Код"), Text("Наименование")]),
    ];

    private static AccessColumn Text(string name) => new(name, "текст", false);

    private static (string Table, Dictionary<string, object?> Row) TaskRow(
        int key, string? number, string? lastName, string? firstName = null, string? fullName = null, object? birth = null,
        string? sex = null, string? residence = null, string? initiator = "ГУ-1", string? justification = "Проверка",
        string? purpose = "Цель", string? measure = null, DateTime? date = null, string? info = null) =>
        (Tasks, new Dictionary<string, object?>
        {
            ["Код"] = key,
            ["№ задания"] = number,
            ["Дата"] = date,
            ["Фамилия"] = lastName,
            ["Имя"] = firstName,
            ["ФИО одним полем"] = fullName,
            ["Год рождения"] = birth,
            ["Пол"] = sex,
            ["Место жительства"] = residence,
            ["Инициатор"] = initiator,
            ["Обоснование"] = justification,
            ["Цель мероприятия"] = purpose,
            ["Мероприятие"] = measure,
            ["Сведения об объекте"] = info,
        });

    private static (string Table, Dictionary<string, object?> Row) LinkRow(
        int key, string? taskNumber, string? linkType, string? name = null, string? plate = null, string? make = null,
        string? owner = null, string? addresses = null) =>
        (Links, new Dictionary<string, object?>
        {
            ["Код"] = key,
            ["№ задания"] = taskNumber,
            ["Связь объекта"] = linkType,
            ["ФИО связи"] = name,
            ["Гос. номер"] = plate,
            ["Марка авто"] = make,
            ["Владелец"] = owner,
            ["Адреса посещения"] = addresses,
        });

    private static FakeAccessDatabase Database(params (string Table, Dictionary<string, object?> Row)[] rows) =>
        new(DepartmentTables(), rows);

    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0xFF, 0xD9];

    private static byte[] Wrap(byte[] image) => [0x15, 0x1C, 0x00, 0x02, .. "Package"u8, 0x00, .. new byte[30], .. image, 0x01, 0x02, 0x03];

    private static byte[] Bmp()
    {
        // Заголовок файла (14) + BITMAPINFOHEADER (40) + один пиксель (4).
        var bmp = new byte[58];
        "BM"u8.CopyTo(bmp);
        BitConverter.GetBytes(58).CopyTo(bmp, 2);
        BitConverter.GetBytes(54).CopyTo(bmp, 10);
        BitConverter.GetBytes(40).CopyTo(bmp, 14);
        return bmp;
    }

    // --- Структура реальной базы отдела: имена и типы таблиц и столбцов как в файле, значения — вымышленные ---

    private const string RealTasks = "задания";
    private const string RealLinks = "связи";
    private const string RealInitiators = "инициатор";

    private void UseRealReferences() =>
        _references.ListAsync(Arg.Any<ReferenceKind?>(), Arg.Any<CancellationToken>()).Returns(
        [
            new ReferenceItemRow(1, ReferenceKind.InitiatorUnit, "ГУ-1", null, 0, true),
            new ReferenceItemRow(4, ReferenceKind.InitiatorUnit, "6 ГУ", null, 0, true),
            new ReferenceItemRow(3, ReferenceKind.LinkType, "Брат", null, 0, true),
        ]);

    private static List<AccessTable> RealTables() =>
    [
        new(RealTasks,
        [
            Typed("Код №", "Long"), Text("№ задания"), Text("Фамилия"), Text("имя"), Text("отчество"), Text("год рождения"),
            Text("пол"), Text("мероприятие"), Typed("место работы", "Memo"), Typed("место жительство", "Memo"), Text("гражданство"),
            Typed("обоснование", "Memo"), Typed("инициатор", "Memo"), Typed("место рождения", "Memo"),
            Typed("сведения об объекте", "Memo"), new("фотография", "объект OLE", true), Text("кличка"),
            Typed("цель мероприятия", "Memo"), Typed("результат мероприятия", "Memo"),
        ]),
        new(RealInitiators,
        [
            Text("№ задания"), Text("инициатор задания"), Text("нач управления"), Text("исполнитель"), Text("отдел"), Text("телефон"),
        ]),
        new(RealLinks,
        [
            Typed("Код связи", "Long"), Text("№ задания"), Text("Кличка объекта"), Typed("Сведения об объекте связи", "Memo"),
            Text("Марка авто"), Text("Гос номер авто"), Text("Владелец авто"), Typed("Адреса посещения связи", "Memo"),
            new("Фото", "объект OLE", true),
        ]),
    ];

    private static AccessColumn Typed(string name, string type) => new(name, type, false);

    // Как в реальной базе: пустые строки, повтор № «1/», задание без №, связи без № и с одним фото, строка инициатора с
    // одним № задания, инициатор «6ГУ»/«6Гу» при записи справочника «6 ГУ».
    private static FakeAccessDatabase RealDatabase() => new(RealTables(),
    [
        RealTask(1, "1/", "Первов", "Первый", "Первович", birth: "1985", sex: "м", measure: "ОН", work: "ООО «Работа»",
            residence: "г. Город, ул. Первая, 1", justification: "ПС №1-2", initiator: "6ГУ", birthPlace: "г. Город",
            info: "сведения", photo: PaintPicture(Bmp()), alias: "Кот", purpose: "установить связи", result: "установлено"),
        RealTask(2, null, "Второв", "Второй", initiator: "6Гу", citizenship: "Кыргызстан"),
        RealTask(3, "1/", "Третьев", "Третий", initiator: "6ГУ"),
        RealTask(4),
        RealTask(5, "5/", "Пятов", "Пятый", initiator: "6ГУ"),
        RealInitiator("9"),
        RealInitiator("5/", unit: "6 ГУ", head: "Начальник Н.Н.", executor: "Исполнитель И.И.", department: "2 отдел", phone: "12-34-56"),
        RealInitiator("1/", executor: "Кто-то К.К."),
        RealLink(1),
        RealLink(2),
        RealLink(3, alias: "Хорёк"),
        RealLink(4, "5/", "Лис", "встречается с объектом", "Toyota", "01KG123ABC", "отец", "ул. А, 1; ул. Б, 2", Wrap(Jpeg)),
        RealLink(5, "1/", "Волк"),
        RealLink(6, photo: Wrap(Jpeg)),
    ]);

    private static (string Table, Dictionary<string, object?> Row) RealTask(
        int key, string? number = null, string? lastName = null, string? firstName = null, string? middleName = null,
        string? birth = null, string? sex = null, string? measure = null, string? work = null, string? residence = null,
        string? citizenship = null, string? justification = null, string? initiator = null, string? birthPlace = null,
        string? info = null, byte[]? photo = null, string? alias = null, string? purpose = null, string? result = null) =>
        (RealTasks, new Dictionary<string, object?>
        {
            ["Код №"] = key,
            ["№ задания"] = number,
            ["Фамилия"] = lastName,
            ["имя"] = firstName,
            ["отчество"] = middleName,
            ["год рождения"] = birth,
            ["пол"] = sex,
            ["мероприятие"] = measure,
            ["место работы"] = work,
            ["место жительство"] = residence,
            ["гражданство"] = citizenship,
            ["обоснование"] = justification,
            ["инициатор"] = initiator,
            ["место рождения"] = birthPlace,
            ["сведения об объекте"] = info,
            ["фотография"] = photo,
            ["кличка"] = alias,
            ["цель мероприятия"] = purpose,
            ["результат мероприятия"] = result,
        });

    private static (string Table, Dictionary<string, object?> Row) RealLink(
        int key, string? task = null, string? alias = null, string? info = null, string? make = null, string? plate = null,
        string? owner = null, string? addresses = null, byte[]? photo = null) =>
        (RealLinks, new Dictionary<string, object?>
        {
            ["Код связи"] = key,
            ["№ задания"] = task,
            ["Кличка объекта"] = alias,
            ["Сведения об объекте связи"] = info,
            ["Марка авто"] = make,
            ["Гос номер авто"] = plate,
            ["Владелец авто"] = owner,
            ["Адреса посещения связи"] = addresses,
            ["Фото"] = photo,
        });

    private static (string Table, Dictionary<string, object?> Row) RealInitiator(
        string? task, string? unit = null, string? head = null, string? executor = null, string? department = null, string? phone = null) =>
        (RealInitiators, new Dictionary<string, object?>
        {
            ["№ задания"] = task,
            ["инициатор задания"] = unit,
            ["нач управления"] = head,
            ["исполнитель"] = executor,
            ["отдел"] = department,
            ["телефон"] = phone,
        });

    // Обёртка OLE «Paint.Picture» — как в поле «фотография» реальной базы: заголовок объекта, имя класса «PBrush», затем
    // файл BMP и хвост (копия картинки для показа).
    private static byte[] PaintPicture(byte[] bmp) =>
    [
        0x15, 0x1C, 0x33, 0x00, 0x02, 0x00, 0x00, 0x00, 0x11, 0x00, 0x0E, 0x00, 0x14, 0x00, 0x25, 0x00, 0xFF, 0xFF, 0xFF, 0xFF,
        .. new byte[16], 0x00, .. "Paint.Picture"u8, 0x00, 0x01, 0x05, 0x00, 0x00, 0x02, 0x00, 0x00, 0x00, 0x07, 0x00, 0x00, 0x00,
        .. "PBrush"u8, 0x00, .. new byte[10], 0xBB, 0x03, 0x00, .. bmp, .. new byte[40],
    ];

    /// <summary>База в памяти: строки отдаются в порядке задания, только запрошенные столбцы.</summary>
    private sealed class FakeAccessDatabase(IReadOnlyList<AccessTable> tables, (string Table, Dictionary<string, object?> Row)[] rows)
        : IAccessDatabase
    {
        public IReadOnlyList<AccessTable> Tables => tables;

        public async IAsyncEnumerable<IReadOnlyDictionary<string, object?>> ReadRowsAsync(
            string table, IReadOnlyCollection<string> columns, string? orderBy, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var (name, row) in rows.Where(r => r.Table == table))
            {
                await System.Threading.Tasks.Task.Yield();
                yield return columns.ToDictionary(c => c, c => row.GetValueOrDefault(c));
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
