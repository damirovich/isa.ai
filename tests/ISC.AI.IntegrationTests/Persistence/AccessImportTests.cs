using System;
using System.Collections.Generic;
using System.Data.OleDb;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.BackgroundTasks;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.Media.Application;
using ISC.AI.Modules.Media.Domain.Model;
using ISC.AI.Modules.Media.Domain.Services;
using ISC.AI.Profile.Investigation.Application.Features.AccessImport;
using ISC.AI.Profile.Investigation.Data;
using ISC.AI.Profile.Investigation.Domain.Enums;
using ISC.AI.Profile.Investigation.Domain.Services;
using NSubstitute;
using Shouldly;
using Testcontainers.PostgreSql;

namespace ISC.AI.IntegrationTests.Persistence;

/// <summary>
/// Перенос базы Access (ADR-0043) на настоящем драйвере ACE и настоящем Postgres: база создаётся в тесте (ADOX + ACE),
/// читается только на чтение из временной копии, которая удаляется; задания, объекты, фото, связи, автомобили и адреса
/// ложатся в рабочие поля теми же хранилищами, что ручной ввод; повтор той же базы дублей не заводит.
/// </summary>
/// <remarks>
/// Нужен Windows и драйвер «Microsoft Access Database Engine» x64 (или Office x64). Его отсутствие — явная ошибка с
/// инструкцией, а не тихий пропуск (как у тестов расшифровки): «перенос не проверен» должно быть видно.
/// </remarks>
[Trait("Category", "Access")]
[SupportedOSPlatform("windows")]
public sealed class AccessImportTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = TestPostgres.Create();
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "iscai-access-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _copies;

    public AccessImportTests()
    {
        _copies = Path.Combine(_folder, "copies");
    }

    public async Task InitializeAsync()
    {
        AccessTestDatabase.EnsureAvailable();
        Directory.CreateDirectory(_folder);
        await _postgres.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _postgres.DisposeAsync();

        // Движок ACE отпускает файлы с задержкой после закрытия соединения — уборка с повторами.
        for (var attempt = 0; Directory.Exists(_folder); attempt++)
        {
            try
            {
                Directory.Delete(_folder, recursive: true);
            }
            catch (IOException) when (attempt < 30)
            {
                await Task.Delay(100);
            }
        }
    }

    [Fact(DisplayName = "ADR-0043: схема и строки читаются драйвером ACE (типы, объект OLE, порядок по коду); временная копия удаляется закрытием")]
    public async Task Reader_reads_schema_and_rows_and_removes_copy()
    {
        var path = CreateSampleDatabase();
        var reader = new OleDbAccessDatabaseReader(_copies);
        reader.UnavailableReason.ShouldBeNull();

        await using (var content = File.OpenRead(path))
        {
            await using var database = await reader.OpenAsync(content, ".accdb");
            Directory.GetFiles(_copies, "*.accdb").Length.ShouldBe(1);

            var tasks = database.Tables.Single(t => t.Name == "Задания");
            tasks.Columns.Select(c => c.Name).ShouldContain("№ задания");
            tasks.Columns.Single(c => c.Name == "Фото").IsBinary.ShouldBeTrue();
            tasks.Columns.Single(c => c.Name == "Фото").TypeName.ShouldBe("объект OLE");
            tasks.Columns.Single(c => c.Name == "Дата").TypeName.ShouldBe("дата");
            tasks.Columns.Single(c => c.Name == "Обоснование").TypeName.ShouldBe("длинный текст");
            database.Tables.ShouldNotContain(t => t.Name.StartsWith("MSys", StringComparison.Ordinal));

            var rows = new List<IReadOnlyDictionary<string, object?>>();
            await foreach (var row in database.ReadRowsAsync("Задания", ["Код", "Фамилия", "Фото"], "Код"))
            {
                rows.Add(row);
            }

            rows.Count.ShouldBe(4);
            rows[0]["Фамилия"].ShouldBe("Иванов");
            rows[0]["Фото"].ShouldBeOfType<byte[]>();
            rows[1]["Фото"].ShouldBeNull();
            rows[3]["Фамилия"].ShouldBeNull("пустая строка — null");

            // Имя не из схемы в SQL не попадает.
            await Should.ThrowAsync<AccessDatabaseReadException>(async () =>
            {
                await foreach (var _ in database.ReadRowsAsync("Задания", ["Фамилия] FROM [MSysObjects]--"], null))
                {
                }
            });
        }

        Directory.GetFiles(_copies).ShouldBeEmpty("копия базы — полный учёт отдела, после закрытия её на диске быть не должно");
    }

    [Fact(DisplayName = "ADR-0043: база с паролем и не-база — понятный отказ, копия не остаётся")]
    public async Task Reader_explains_password_and_garbage()
    {
        var protectedPath = Path.Combine(_folder, "protected.accdb");
        AccessTestDatabase.Create(protectedPath, password: "секрет");
        var reader = new OleDbAccessDatabaseReader(_copies);

        await using (var content = File.OpenRead(protectedPath))
        {
            var error = await Should.ThrowAsync<AccessDatabaseReadException>(() => reader.OpenAsync(content, ".accdb"));
            error.Message.ShouldBe(OleDbAccessDatabaseReader.PasswordMessage);
        }

        await using (var garbage = new MemoryStream("это не база"u8.ToArray()))
        {
            var error = await Should.ThrowAsync<AccessDatabaseReadException>(() => reader.OpenAsync(garbage, ".mdb"));
            error.Message.ShouldBe(OleDbAccessDatabaseReader.NotDatabaseMessage);
        }

        Directory.GetFiles(_copies).ShouldBeEmpty();
    }

    [Fact(DisplayName = "ADR-0043: проверка и перенос — дела-задания, объекты с анкетой, фото, связи с автомобилем и адресами; повтор без дублей")]
    public async Task Import_creates_tasks_objects_links_and_is_idempotent()
    {
        var kit = await ArrangeAsync();
        var gu = await CreateReferenceAsync(kit.References, ReferenceKind.InitiatorUnit, "ГУ-1");
        var brother = await CreateReferenceAsync(kit.References, ReferenceKind.LinkType, "брат");
        var file = await SaveSampleAsync(kit);

        AccessMapping mapping;
        await using (var database = await kit.Files.OpenAsync(kit.Reader, file, OperatorId, CancellationToken.None))
        {
            mapping = AccessAutoMapper.Suggest(database.Tables);
        }

        mapping.TasksTable.ShouldBe("Задания");
        mapping.LinksTable.ShouldBe("Связи");
        mapping.TaskColumns[AccessField.Photo].ShouldBe("Фото");
        mapping.LinkColumns[AccessField.CarPlate].ShouldBe("Гос номер");

        // Без галочки «завести недостающие» — задание без инициатора и связь «знакомый» не переносятся.
        var strict = new AccessImportOptions(Division, Grif, CaseStatus.Suspended, CreateMissingReferences: false);
        var plan = await PlanAsync(kit, file, mapping, strict);
        plan.TaskCount.ShouldBe(4);
        plan.NewTaskCount.ShouldBe(1);
        plan.Problems.ShouldContain(p => p.Key == "2" && p.Message.Contains(AccessImporter.PlaceholderInitiator, StringComparison.Ordinal));
        plan.Problems.ShouldContain(p => p.Key == "3" && p.Message.Contains("повтор № задания", StringComparison.Ordinal));
        plan.Problems.ShouldContain(p => p.Key == "4" && p.Message.Contains("нет ФИО", StringComparison.Ordinal));
        plan.Problems.ShouldContain(p => p.Table == "Связи" && p.Message.Contains("99/24", StringComparison.Ordinal));

        var options = strict with { CreateMissingReferences = true };
        plan = await PlanAsync(kit, file, mapping, options);
        plan.NewTaskCount.ShouldBe(2);
        plan.NewLinkCount.ShouldBe(2);
        plan.PhotoCount.ShouldBe(1);
        plan.InitiatorsToCreate.ShouldBe([AccessImporter.PlaceholderInitiator]);
        plan.LinkTypesToCreate.ShouldBe(["знакомый"]);
        plan.Sample.Select(s => s.CaseNumber).ShouldBe(["12/24", "13/24"]);

        var report = await RunAsync(kit, file, mapping, options);
        report.Stopped.ShouldBeFalse();
        report.CasesCreated.ShouldBe(2);
        report.ObjectsCreated.ShouldBe(2);
        report.LinksCreated.ShouldBe(2);
        report.PhotosAccepted.ShouldBe(1);
        report.VehiclesSaved.ShouldBe(1);
        report.AddressesSaved.ShouldBe(2);
        report.ReferencesCreated.ShouldBe(2);

        // Дело-задание: реквизиты, инициатор из справочника, заменитель пустых реквизитов, статус.
        var access = kit.Access;
        var page = await kit.Cases.ListAsync(new CaseFilter(), access);
        page.TotalCount.ShouldBe(2);
        var first = (await kit.Cases.GetAsync(page.Rows.Single(r => r.TaskNumber == "12/24").Id, access)).ShouldNotBeNull();
        first.Kind.ShouldBe(CaseKind.ObjectTask);
        first.Status.ShouldBe(CaseStatus.Suspended);
        first.Classification.ShouldBe(Grif);
        first.OpenedAt.ShouldBe(new DateOnly(2024, 3, 5));
        var requisites = first.TaskRequisites.ShouldNotBeNull();
        requisites.InitiatorUnitId.ShouldBe(gu);
        requisites.Justification.ShouldBe("Проверка сведений");
        requisites.Notes.ShouldNotBeNull().ShouldContain("Мероприятие: НН");
        requisites.Notes.ShouldContain("Результат мероприятия: связи установлены");
        var second = (await kit.Cases.GetAsync(page.Rows.Single(r => r.TaskNumber == "13/24").Id, access)).ShouldNotBeNull();
        second.TaskRequisites.ShouldNotBeNull().Justification.ShouldBe(AccessImporter.NotSpecified);

        // Объект и связь.
        var persons = await kit.Persons.ListByCaseAsync(first.Id, access);
        var target = persons.Single(p => p.Role == PersonRole.Target);
        target.DisplayName.ShouldBe("Иванов Иван Петрович");
        var questionnaire = target.Questionnaire.ShouldNotBeNull();
        questionnaire.BirthYear.ShouldBe(1980);
        questionnaire.Sex.ShouldBe(PersonSex.Male);
        questionnaire.Alias.ShouldBe("Шторм");
        target.Notes.ShouldNotBeNull().ShouldContain("Сведения об объекте: работает таксистом");
        var link = persons.Single(p => p.Role == PersonRole.Link);
        link.IsUnidentified.ShouldBeTrue();
        link.LinkedToPersonId.ShouldBe(target.Id);
        link.LinkTypeId.ShouldBe(brother);
        var linkRequisites = (await kit.Requisites.GetAsync(link.Id, access)).ShouldNotBeNull();
        var vehicle = linkRequisites.Vehicles.ShouldHaveSingleItem();
        vehicle.PlateNumber.ShouldBe("01KG123ABC");
        vehicle.Make.ShouldBe("Toyota Camry");
        vehicle.Notes.ShouldBe("Владелец: Иванов И.");
        linkRequisites.Addresses.Select(a => a.Text).ShouldBe(["ул. Киевская, 5", "мкр. Джал, 10"], ignoreOrder: true);
        linkRequisites.Addresses.ShouldAllBe(a => a.Kind == AddressKind.Other);

        // Фото — общим приёмом в материалы дела с грифом дела, обработка поставлена.
        await kit.Media.Received(1).ReceiveAsync(
            Arg.Is<MediaAssetDraft>(d => d.ContentType == "image/jpeg" && d.Classification == Grif && d.DivisionId == Division),
            Arg.Any<Stream>(), Arg.Any<CancellationToken>());
        await kit.Queue.Received(1).EnqueueAsync(Arg.Any<string>(), Arg.Any<Func<IServiceProvider, CancellationToken, Task>>(), Arg.Any<CancellationToken>());
        await kit.Audit.Received().WriteAsync(
            Arg.Is<AuditEntry>(e => e.Action == AuditAction.Ingest && e.PayloadSensitive!.Contains("дело № 12/24", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());

        // Повтор той же базы — ничего нового.
        var again = await RunAsync(kit, file, mapping, options);
        again.CasesCreated.ShouldBe(0);
        again.LinksCreated.ShouldBe(0);
        again.AlreadyImported.ShouldBe(2);
        (await kit.Cases.ListAsync(new CaseFilter(), access)).TotalCount.ShouldBe(2);
        (await PlanAsync(kit, file, mapping, options)).HasWork.ShouldBeFalse();
    }

    [Fact(DisplayName = "ADR-0043: гриф выше допуска — перенос останавливается на первой записи, ничего не заведено")]
    public async Task Import_stops_outside_clearance()
    {
        var kit = await ArrangeAsync(maxClassification: 1);
        await CreateReferenceAsync(kit.References, ReferenceKind.InitiatorUnit, "ГУ-1");
        var file = await SaveSampleAsync(kit);
        AccessMapping mapping;
        await using (var database = await kit.Files.OpenAsync(kit.Reader, file, OperatorId, CancellationToken.None))
        {
            mapping = AccessAutoMapper.Suggest(database.Tables);
        }

        var report = await RunAsync(kit, file, mapping, new AccessImportOptions(Division, Grif, CaseStatus.InProgress, true));

        report.Stopped.ShouldBeTrue();
        report.CasesCreated.ShouldBe(0);
        (await kit.Cases.ListAsync(new CaseFilter(), kit.Access)).TotalCount.ShouldBe(0);
    }

    [Fact(DisplayName = "ТБ-077: файл базы, изменённый в хранилище после загрузки, не открывается")]
    public async Task Changed_stored_file_is_refused()
    {
        var kit = await ArrangeAsync();
        var file = await SaveSampleAsync(kit);
        var stored = Path.Combine(_folder, "storage", AccessImportFiles.Category, OperatorId.ToString(System.Globalization.CultureInfo.InvariantCulture), file.StoredFileName);
        File.Exists(stored).ShouldBeTrue();
        await using (var tamper = new FileStream(stored, FileMode.Append))
        {
            tamper.WriteByte(0);
        }

        var error = await Should.ThrowAsync<AccessDatabaseReadException>(
            () => kit.Files.OpenAsync(kit.Reader, file, OperatorId, CancellationToken.None));
        error.Message.ShouldBe(AccessImportFiles.ChangedMessage);
        Directory.GetFiles(_copies).ShouldBeEmpty();
    }

    private const int OperatorId = 10;
    private const int Division = 5;
    private const short Grif = 2;

    private async Task<AccessDatabaseFile> SaveSampleAsync(Kit kit)
    {
        await using var source = File.OpenRead(CreateSampleDatabase());
        return await kit.Files.SaveAsync(source, "otdel.accdb", OperatorId, CancellationToken.None);
    }

    private static async Task<AccessImportPlan> PlanAsync(Kit kit, AccessDatabaseFile file, AccessMapping mapping, AccessImportOptions options)
    {
        await using var database = await kit.Files.OpenAsync(kit.Reader, file, OperatorId, CancellationToken.None);
        return await kit.Importer.PlanAsync(database, file, mapping, options);
    }

    private static async Task<AccessImportReport> RunAsync(Kit kit, AccessDatabaseFile file, AccessMapping mapping, AccessImportOptions options)
    {
        await using var database = await kit.Files.OpenAsync(kit.Reader, file, OperatorId, CancellationToken.None);
        return await kit.Importer.RunAsync(database, file, mapping, options, kit.Access, OperatorId);
    }

    private async Task<Kit> ArrangeAsync(short maxClassification = 4)
    {
        var factory = new InvestigationContextFactory(_postgres.GetConnectionString());
        var core = new CoreContextFactory(_postgres.GetConnectionString());
        await InvestigationTestKit.MigrateAsync(factory);
        await InvestigationTestKit.AssignRolesAsync(factory, (OperatorId, InvestigationRole.Administrator));

        var policy = new InvestigationAccessPolicy(factory);
        var roles = new UserRoleStore(core, factory);
        var cases = InvestigationTestKit.CreateCaseStore(factory, core);
        var persons = InvestigationTestKit.CreatePersonStore(factory, core);
        var requisites = new PersonRequisiteStore(factory, policy, roles);
        var references = new ReferenceStore(factory);

        var media = Substitute.For<IMediaStore>();
        media.ReceiveAsync(Arg.Any<MediaAssetDraft>(), Arg.Any<Stream>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                // Хранилище дочитывает поток (как настоящее): так приём проверяет фактическую длину.
                await call.Arg<Stream>().CopyToAsync(Stream.Null);
                return new MediaAssetReceipt(77, Duplicate: false);
            });
        var queue = Substitute.For<IBackgroundTaskQueue>();

        // Настройка подмены NSubstitute: ValueTask здесь не исполняется, а задаёт ответ (CA2012 к этому неприменим).
#pragma warning disable CA2012
        queue.EnqueueAsync(Arg.Any<string>(), Arg.Any<Func<IServiceProvider, CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<Guid>(Guid.NewGuid()));
#pragma warning restore CA2012
        var audit = Substitute.For<IAuditWriter>();

        var storage = new ISC.AI.Persistence.Storage.LocalFileStorage(Path.Combine(_folder, "storage"));
        var importer = new AccessImporter(
            new AccessImportRecordStore(factory), references, cases, persons, requisites,
            InvestigationTestKit.CreateCaseScope(factory, core), media, MediaUploadOptions.Default, queue, audit, TimeProvider.System);
        return new Kit(
            new AccessImportFiles(storage), new OleDbAccessDatabaseReader(_copies), importer, cases, persons, requisites, references,
            media, queue, audit, InvestigationTestKit.Access(OperatorId, maxClassification, Division));
    }

    private static async Task<int> CreateReferenceAsync(ReferenceStore references, ReferenceKind kind, string name)
    {
        var (result, id) = await references.CreateAsync(kind, name, null, 0);
        result.ShouldBe(ReferenceWriteResult.Ok);
        return id;
    }

    /// <summary>
    /// База как у отдела: «Задания» (личные и оперативные данные, фото в поле OLE) и «Связи». Записи: полное задание с
    /// фото; задание без инициатора и обоснования; повтор № задания; запись без ФИО. Связи: брат с автомобилем и двумя
    /// адресами, знакомый, связь к несуществующему заданию.
    /// </summary>
    private string CreateSampleDatabase()
    {
        var path = Path.Combine(_folder, Guid.NewGuid().ToString("N") + ".accdb");
        AccessTestDatabase.Create(path);
        AccessTestDatabase.Execute(path,
            "CREATE TABLE [Задания] ([Код] COUNTER PRIMARY KEY, [№ задания] TEXT(50), [Дата] DATETIME, [Фамилия] TEXT(100), "
            + "[Имя] TEXT(100), [Отчество] TEXT(100), [Год рождения] TEXT(20), [Пол] TEXT(10), [Место жительства] TEXT(255), "
            + "[Кличка] TEXT(100), [Инициатор] TEXT(255), [Обоснование] MEMO, [Цель мероприятия] MEMO, [Сведения об объекте] MEMO, "
            + "[Мероприятие] TEXT(255), [Результат мероприятия] MEMO, [Фото] LONGBINARY)");
        AccessTestDatabase.Execute(path,
            "CREATE TABLE [Связи] ([Код] COUNTER PRIMARY KEY, [№ задания] TEXT(50), [Связь объекта] TEXT(100), "
            + "[Сведения об объекте связи] MEMO, [Марка авто] TEXT(100), [Гос номер] TEXT(20), [Владелец] TEXT(255), [Адреса посещения] MEMO)");

        const string insertTask =
            "INSERT INTO [Задания] ([№ задания], [Дата], [Фамилия], [Имя], [Отчество], [Год рождения], [Пол], [Место жительства], "
            + "[Кличка], [Инициатор], [Обоснование], [Цель мероприятия], [Сведения об объекте], [Мероприятие], [Результат мероприятия], [Фото]) "
            + "VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)";
        AccessTestDatabase.Execute(path, insertTask, "12/24", new DateTime(2024, 3, 5), "Иванов", "Иван", "Петрович", "1980", "м",
            "г. Бишкек, ул. Ленина, 1", "Шторм", "ГУ-1", "Проверка сведений", "Установить связи", "работает таксистом", "НН",
            "связи установлены", OleWrappedJpeg());
        AccessTestDatabase.Execute(path, insertTask, "13/24", null, "Петрова", null, null, "1975 г.р.", "ж", null, null, null, null, null,
            null, null, null, null);
        AccessTestDatabase.Execute(path, insertTask, "13/24", null, "Сидоров", null, null, null, null, null, null, "ГУ-1", null, null,
            null, null, null, null);
        AccessTestDatabase.Execute(path, insertTask, "14/24", null, null, null, null, null, null, null, null, "ГУ-1", null, null,
            null, null, null, null);

        const string insertLink =
            "INSERT INTO [Связи] ([№ задания], [Связь объекта], [Сведения об объекте связи], [Марка авто], [Гос номер], [Владелец], "
            + "[Адреса посещения]) VALUES (?, ?, ?, ?, ?, ?, ?)";
        AccessTestDatabase.Execute(path, insertLink, "12/24", "брат", null, "Toyota Camry", "01KG123ABC", "Иванов И.",
            "ул. Киевская, 5; мкр. Джал, 10");
        AccessTestDatabase.Execute(path, insertLink, "13/24", "знакомый", "встречаются по пятницам", null, null, null, null);
        AccessTestDatabase.Execute(path, insertLink, "99/24", "друг", null, null, null, null, null);
        return path;
    }

    /// <summary>Снимок JPEG внутри обёртки OLE: заголовок «Package» перед картинкой и хвост после неё.</summary>
    private static byte[] OleWrappedJpeg()
    {
        byte[] jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0x00, 0x01, 0x01, 0x00,
            0x00, 0x01, 0x00, 0x01, 0x00, 0x00, 0xFF, 0xD9];
        return [.. "\u0015\u001c\u0000\u0002Package\u0000"u8, .. new byte[40], .. jpeg, .. new byte[] { 0x01, 0x02, 0x03, 0x04 }];
    }

    private sealed record Kit(
        AccessImportFiles Files,
        OleDbAccessDatabaseReader Reader,
        AccessImporter Importer,
        CaseStore Cases,
        PersonStore Persons,
        PersonRequisiteStore Requisites,
        ReferenceStore References,
        IMediaStore Media,
        IBackgroundTaskQueue Queue,
        IAuditWriter Audit,
        AccessContext Access);
}

/// <summary>Создание тестовых баз Access: файл — через ADOX, таблицы и строки — через ACE OLE DB.</summary>
[SupportedOSPlatform("windows")]
internal static class AccessTestDatabase
{
    private static readonly Lazy<string?> Provider = new(FindProvider);

    /// <summary>Падает с инструкцией, если на машине нет Windows, ACE x64 или ADOX.</summary>
    public static void EnsureAvailable()
    {
        if (!OperatingSystem.IsWindows() || Provider.Value is null || Type.GetTypeFromProgID("ADOX.Catalog") is null)
        {
            throw new InvalidOperationException(
                "Для тестов переноса из Access нужен Windows и драйвер «Microsoft Access Database Engine» x64 (или Office x64): "
                + "установите его либо исключите категорию: dotnet test --filter \"Category!=Access\".");
        }
    }

    /// <summary>Создаёт пустую базу (с паролем — зашифрованную).</summary>
    public static void Create(string path, string? password = null)
    {
        var type = Type.GetTypeFromProgID("ADOX.Catalog") ?? throw new InvalidOperationException("ADOX недоступен.");
        var catalog = Activator.CreateInstance(type) ?? throw new InvalidOperationException("ADOX недоступен.");
        try
        {
            var connectionString = $"Provider={Provider.Value};Data Source={path};"
                + (password is null ? string.Empty : $"Jet OLEDB:Database Password={password};");
            var connection = type.InvokeMember("Create", BindingFlags.InvokeMethod, null, catalog, [connectionString],
                System.Globalization.CultureInfo.InvariantCulture);
            if (connection is not null)
            {
                connection.GetType().InvokeMember("Close", BindingFlags.InvokeMethod, null, connection, null,
                    System.Globalization.CultureInfo.InvariantCulture);
                Marshal.FinalReleaseComObject(connection);
            }
        }
        finally
        {
            Marshal.FinalReleaseComObject(catalog);
        }
    }

    /// <summary>Выполняет команду с позиционными параметрами (<c>?</c>).</summary>
    public static void Execute(string path, string sql, params object?[] parameters)
    {
        using var connection = new OleDbConnection($"Provider={Provider.Value};Data Source={path};OLE DB Services=-4;");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var value in parameters)
        {
            var parameter = command.CreateParameter();
            (parameter.OleDbType, parameter.Value) = value switch
            {
                null => (OleDbType.VarWChar, (object)DBNull.Value),
                byte[] bytes => (OleDbType.LongVarBinary, bytes),
                DateTime date => (OleDbType.Date, date),
                int number => (OleDbType.Integer, number),
                _ => (OleDbType.VarWChar, value),
            };
            command.Parameters.Add(parameter);
        }

        command.ExecuteNonQuery();
    }

    private static string? FindProvider()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        using var sources = OleDbEnumerator.GetRootEnumerator();
        var registered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (sources.Read())
        {
            if (sources["SOURCES_NAME"] is string name)
            {
                registered.Add(name);
            }
        }

        return OleDbAccessDatabaseReader.Providers.FirstOrDefault(registered.Contains);
    }
}
