using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.BackgroundTasks;
using ISC.AI.Abstractions.Security;
using ISC.AI.Backup;
using ISC.AI.Modules.Media.Application;
using ISC.AI.Modules.Media.Application.Features.Assets;
using ISC.AI.Modules.Media.Data;
using ISC.AI.Modules.Media.Data.Entities;
using ISC.AI.Modules.Media.Domain.Model;
using ISC.AI.Modules.Media.Domain.Services;
using ISC.AI.Persistence.Audit;
using ISC.AI.Persistence.Security;
using ISC.AI.Persistence.Storage;
using ISC.AI.Profile.Investigation.Data.Exchange;
using ISC.AI.Profile.Investigation.Domain.Entities;
using ISC.AI.Profile.Investigation.Domain.Enums;
using ISC.AI.Profile.Investigation.Domain.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Shouldly;
using Testcontainers.PostgreSql;

namespace ISC.AI.IntegrationTests.Persistence;

/// <summary>
/// Обмен между офисами, сторона получателя (ТФ-ОБМ-02, ТБ-083, ЦС-08, ADR-0068 v0.2), на двух настоящих базах: «Офис №2»
/// формирует пакет за месяц, «Офис №1» принимает его в свою базу и своё хранилище. Проверяется: перенос дел, объектов с
/// реквизитами, связей, событий связи и сводок с грифом источника, приём файлов через общий приём «Медиа» побайтно и с
/// постановкой в обработку, сопоставление подразделений по коду и сотрудников по имени входа, отсутствие дублей при повторе,
/// конфликты дел и файлов, отказы до записи (нет подразделения, нет отметки шлюза, неверный пароль, подмена, чужая
/// подпись), акт приёма в журнале аудита.
/// </summary>
/// <remarks>Требуется Docker. Ключи — 3072 бита (наименьшие допустимые): тесты быстрее.</remarks>
public sealed class ExchangeImportTests : IAsyncLifetime
{
    private const string SigningPassword = "пароль-хранителя-подписи";
    private const string ReceivingPassword = "пароль-хранителя-приёма";
    private const string TargetCase = "ОУ-2/17-2026";
    private const string TaskCase = "ОУ-2/18-2026";
    private const int SourceAdminId = 1203;
    private const int ReceiverAdminId = 2201;

    // Сигнатуры настоящих форматов — общий приём «Медиа» проверяет содержимое по сигнатуре (ТС-010).
    private static readonly byte[] VideoBytes =
        [0, 0, 0, 0x18, (byte)'f', (byte)'t', (byte)'y', (byte)'p', (byte)'i', (byte)'s', (byte)'o', (byte)'m', .. Encoding.UTF8.GetBytes(new string('в', 5000))];

    private static readonly byte[] PhotoBytes = [0xFF, 0xD8, 0xFF, 0xE0, .. Encoding.UTF8.GetBytes("снимок " + new string('ф', 3000))];

    private readonly PostgreSqlContainer _postgres = TestPostgres.Create();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "iscai-exchange-import", Guid.NewGuid().ToString("N"));

    private CoreContextFactory _core = null!;
    private MediaContextFactory _media = null!;
    private InvestigationContextFactory _investigation = null!;
    private CoreContextFactory _rxCore = null!;
    private MediaContextFactory _rxMedia = null!;
    private InvestigationContextFactory _rxInvestigation = null!;
    private RSA _recipient = null!;
    private RSA _signing = null!;

    private string SourceStorage => Path.Combine(_root, "source-storage");

    private string ReceiverStorage => Path.Combine(_root, "receiver-storage");

    private string Medium => Path.Combine(_root, "medium");

    private string Keys => Path.Combine(_root, "keys");

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        var source = _postgres.GetConnectionString();
        await using (var connection = new NpgsqlConnection(source))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand("CREATE DATABASE receiver", connection);
            await command.ExecuteNonQueryAsync();
        }

        var receiver = new NpgsqlConnectionStringBuilder(source) { Database = "receiver" }.ConnectionString;
        (_core, _media, _investigation) = (new CoreContextFactory(source), new MediaContextFactory(source), new InvestigationContextFactory(source));
        (_rxCore, _rxMedia, _rxInvestigation) = (new CoreContextFactory(receiver), new MediaContextFactory(receiver), new InvestigationContextFactory(receiver));
        foreach (var (core, media, investigation) in new[] { (_core, _media, _investigation), (_rxCore, _rxMedia, _rxInvestigation) })
        {
            await using (var db = core.CreateDbContext())
            {
                await db.Database.MigrateAsync();
            }

            await using (var db = media.CreateDbContext())
            {
                await db.Database.MigrateAsync();
            }

            await InvestigationTestKit.MigrateAsync(investigation);
        }

        Directory.CreateDirectory(SourceStorage);
        Directory.CreateDirectory(ReceiverStorage);
        Directory.CreateDirectory(Medium);
        Directory.CreateDirectory(Keys);
        _recipient = RSA.Create(BackupKeys.MinKeyBits);
        _signing = RSA.Create(BackupKeys.MinKeyBits);
        await File.WriteAllTextAsync(Path.Combine(Keys, "recipient-public.pem"), BackupKeys.ExportPublicPem(_recipient));
        await File.WriteAllTextAsync(Path.Combine(Keys, "recipient-private.pem"), BackupKeys.ExportPrivatePem(_recipient, ReceivingPassword));
        await File.WriteAllTextAsync(Path.Combine(Keys, "signing-public.pem"), BackupKeys.ExportPublicPem(_signing));
        await File.WriteAllTextAsync(Path.Combine(Keys, "signing-private.pem"), BackupKeys.ExportPrivatePem(_signing, SigningPassword));
    }

    public async Task DisposeAsync()
    {
        _recipient.Dispose();
        _signing.Dispose();
        await _postgres.DisposeAsync();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact(DisplayName = "ТФ-ОБМ-02: пакет Офиса №2 принят Офисом №1 — дела, объекты с реквизитами, связи, события, сводки с грифом источника; файлы побайтно и в обработку; акт приёма")]
    public async Task Package_is_received_into_the_other_office()
    {
        var world = await SeedSourceAsync();
        var receiverExpert = await SeedReceiverAsync(withDivision: true);
        var package = await ExportAsync(world.Period);
        var (service, channel, queue) = BuildReceiver();

        var import = await ImportAsync(service, channel, package);

        import.Status.ShouldBe(ExchangeImportStatus.Succeeded, import.Error);
        (import.CasesCreated, import.PersonsCreated, import.EventsCreated, import.ReportsCreated).ShouldBe((2, 3, 1, 1));
        (import.AssetsCreated, import.FilesReceived, import.BytesReceived).ShouldBe((2, 2, VideoBytes.Length + (long)PhotoBytes.Length));
        (import.Conflicts, import.AlreadyImported, import.MissingFiles, import.Rejected, import.Deferred).ShouldBe((0, 0, 0, 0, 1));
        (import.SourceInstance, import.GatewayConfirmed, import.Classification).ShouldBe(("office-2", true, ClassificationLevels.Secret));

        await using var db = _rxInvestigation.CreateDbContext();
        var division = await db.Divisions.SingleAsync(d => d.Code == "оу2");
        var target = await db.Cases.SingleAsync(c => c.Number == TargetCase);
        (target.DivisionId, target.Classification, target.InvestigatorUserId).ShouldBe((division.Id, ClassificationLevels.ForOfficialUse, receiverExpert));
        var task = await db.Cases.SingleAsync(c => c.Number == TaskCase);
        (task.Kind, task.TaskNumber, task.Classification).ShouldBe((CaseKind.ObjectTask, "З-77", ClassificationLevels.Secret));
        (await db.ReferenceItems.SingleAsync(r => r.Id == task.InitiatorUnitId)).Name.ShouldBe("ГУ уголовного розыска");

        var persons = await db.Persons.Where(p => p.CaseId == target.Id).ToListAsync();
        var person = persons.Single(p => p.Role == PersonRole.Target);
        (person.Classification, person.NameNormalized).ShouldBe((ClassificationLevels.Secret, RequisiteNormalizer.PersonName("Иванов Иван")));
        var link = persons.Single(p => p.Role == PersonRole.Link);
        link.LinkedToPersonId.ShouldBe(person.Id);
        (await db.ReferenceItems.SingleAsync(r => r.Id == link.LinkTypeId)).Name.ShouldBe("брат");
        (await db.PersonPhones.SingleAsync(p => p.PersonId == person.Id)).NumberNormalized.ShouldBe(RequisiteNormalizer.Phone("0555 123 456"));
        (await db.PersonAddresses.SingleAsync(a => a.PersonId == person.Id)).TextNormalized.ShouldBe(RequisiteNormalizer.Address("г. Бишкек, ул. Ленина, 15"));
        (await db.PersonVehicles.SingleAsync(v => v.PersonId == person.Id)).PlateNormalized.ShouldBe(RequisiteNormalizer.Plate("01KG123ABC"));
        var unidentified = await db.Persons.SingleAsync(p => p.CaseId == task.Id);
        (unidentified.IsUnidentified, unidentified.UnidentifiedNumber).ShouldBe((true, 1));

        var linkEvent = await db.LinkEvents.Include(e => e.Participants).SingleAsync();
        linkEvent.Participants.Select(p => p.PersonId).ShouldBe([person.Id, unidentified.Id], ignoreOrder: true);
        var report = await db.CaseReports.SingleAsync(r => r.CaseId == target.Id);
        report.SearchText.ShouldContain(RequisiteNormalizer.SearchText("встреча подтверждена"));
        (await db.CaseReportRevisions.SingleAsync(v => v.ReportId == report.Id)).Number.ShouldBe(1);

        // Файлы — побайтно те же, в хранилище получателя, привязаны к делам, поставлены в обработку.
        await using var media = _rxMedia.CreateDbContext();
        var assets = await media.Assets.OrderBy(a => a.Id).ToListAsync();
        assets.Select(a => a.ContentHash).ShouldBe([Hash(VideoBytes), Hash(PhotoBytes)], ignoreOrder: true);
        var photo = assets.Single(a => a.ContentHash == Hash(PhotoBytes));
        photo.Classification.ShouldBe(ClassificationLevels.Secret);
        photo.Source.ShouldNotBeNull().ShouldStartWith("Принято из office-2");
        var stored = Path.Combine(ReceiverStorage, MediaFileCategories.Originals, photo.Id.ToString(CultureInfo.InvariantCulture), photo.StoredFileName);
        (await File.ReadAllBytesAsync(stored)).ShouldBe(PhotoBytes);
        var links = await db.CaseMediaLinks.ToListAsync();
        links.ShouldContain(l => l.CaseId == target.Id && l.Place == "ул. Ленина, 15");
        links.ShouldContain(l => l.CaseId == task.Id && l.MediaAssetId == photo.Id);
        queue.Kinds.Count(k => k == MediaProcessing.IndexingTaskKind).ShouldBe(2);

        // Карта: каждая заведённая запись сопоставлена ровно раз.
        (await db.ExchangeSourceRecords.CountAsync(r => r.SourceInstance == "office-2" && r.Kind == ExchangeRecordKind.Case)).ShouldBe(2);
        (await db.ExchangeSourceRecords.CountAsync(r => r.Kind == ExchangeRecordKind.Asset)).ShouldBe(2);

        // Акт приёма — с грифом пакета, счётчиками и без пароля.
        await using var core = _rxCore.CreateDbContext();
        var act = await core.AuditRecords.SingleAsync(r => r.ObjectRef == "investigation:exchange:import:" + package);
        (act.Action, act.Classification, act.SubjectId).ShouldBe((AuditAction.Ingest, ClassificationLevels.Secret, ReceiverAdminId));
        act.PayloadSensitive.ShouldNotBeNull().ShouldContain("заведено: дел 2");
        act.PayloadSensitive.ShouldNotContain(ReceivingPassword);
    }

    [Fact(DisplayName = "ЦС-08: повторный приём того же пакета дублей не заводит — всё «уже принято», файлы не повторяются")]
    public async Task Repeat_import_adds_nothing()
    {
        var world = await SeedSourceAsync();
        await SeedReceiverAsync(withDivision: true);
        var package = await ExportAsync(world.Period);
        var (service, channel, queue) = BuildReceiver();
        (await ImportAsync(service, channel, package)).Status.ShouldBe(ExchangeImportStatus.Succeeded);
        var counts = await CountReceiverAsync();

        var second = await ImportAsync(service, channel, package);

        second.Status.ShouldBe(ExchangeImportStatus.Succeeded, second.Error);
        (second.CasesCreated, second.PersonsCreated, second.AssetsCreated, second.FilesReceived).ShouldBe((0, 0, 0, 0));
        second.AlreadyImported.ShouldBeGreaterThanOrEqualTo(2 + 3 + 1 + 1 + 2);
        (await CountReceiverAsync()).ShouldBe(counts);
        queue.Kinds.Count(k => k == MediaProcessing.IndexingTaskKind).ShouldBe(2);
    }

    [Fact(DisplayName = "ТФ-ОБМ-02: дело с тем же номером и тот же файл у получателя — конфликты для Руководителя; содержимое конфликтного дела не переносится")]
    public async Task Conflicts_wait_for_head()
    {
        var world = await SeedSourceAsync();
        await SeedReceiverAsync(withDivision: true);
        int localCase, localAsset;
        await using (var db = _rxInvestigation.CreateDbContext())
        {
            var division = await db.Divisions.SingleAsync(d => d.Code == "оу2");
            var existing = new CaseFile
            {
                Number = TargetCase, Title = "Своё дело", Kind = CaseKind.CriminalCase, OpenedAt = new DateOnly(2026, 2, 1),
                DivisionId = division.Id, Classification = ClassificationLevels.ForOfficialUse, Status = CaseStatus.InProgress,
            };
            db.Cases.Add(existing);
            await db.SaveChangesAsync();
            localCase = existing.Id;
            localAsset = await StoreAssetAsync(_rxMedia, ReceiverStorage, PhotoBytes, MediaKind.Image, "image/jpeg", ClassificationLevels.Secret, division.Id);
        }

        var package = await ExportAsync(world.Period);
        var (service, channel, _) = BuildReceiver();

        var import = await ImportAsync(service, channel, package);

        import.Status.ShouldBe(ExchangeImportStatus.Succeeded, import.Error);
        (import.CasesCreated, import.Conflicts).ShouldBe((1, 2));
        await using var rx = _rxInvestigation.CreateDbContext();
        var conflicts = await rx.ExchangeImportConflicts.OrderBy(c => c.Kind).ToListAsync();
        conflicts.Select(c => (c.Kind, c.LocalId, c.Status)).ShouldBe(
            [(ExchangeConflictKind.Case, (int?)localCase, ExchangeConflictStatus.Pending), (ExchangeConflictKind.Asset, localAsset, ExchangeConflictStatus.Pending)]);
        conflicts[0].Label.ShouldBe(TargetCase);
        (await rx.Persons.CountAsync(p => p.CaseId == localCase)).ShouldBe(0);
        (await rx.CaseReports.CountAsync()).ShouldBe(0);
        await using (var media = _rxMedia.CreateDbContext())
        {
            (await media.Assets.CountAsync()).ShouldBe(1);
        }

        // Повторный приём не множит очередь конфликтов.
        await ImportAsync(service, channel, package);
        (await rx.ExchangeImportConflicts.CountAsync()).ShouldBe(2);

        // Своё дело переименовали — совпадения больше нет: следующий приём заводит дело источника, конфликт снят; конфликт
        // файла (свой носитель на месте) по-прежнему ждёт Руководителя.
        await rx.Cases.Where(c => c.Id == localCase).ExecuteUpdateAsync(s => s.SetProperty(c => c.Number, "ОН/1-2026"));
        var third = await ImportAsync(service, channel, package);
        (third.CasesCreated, third.Conflicts).ShouldBe((1, 1));
        (await rx.ExchangeImportConflicts.AsNoTracking().OrderBy(c => c.Kind).Select(c => c.Status).ToListAsync())
            .ShouldBe([ExchangeConflictStatus.Cleared, ExchangeConflictStatus.Pending]);
        (await rx.Cases.CountAsync(c => c.Number == TargetCase)).ShouldBe(1);
    }

    [Fact(DisplayName = "Живая проверка 06.11.2026: два дела источника с одной парой «ГУ + № задания» — оба принимаются, это не конфликт со своей же записью")]
    public async Task Twin_cases_of_one_source_are_not_conflicts()
    {
        var world = await SeedSourceAsync();
        await using (var db = _investigation.CreateDbContext())
        {
            // Как перенос из Access: та же пара «ГУ + № задания», номер дела — с пометкой записи.
            var task = await db.Cases.AsNoTracking().SingleAsync(c => c.Number == TaskCase);
            db.Cases.Add(new CaseFile
            {
                Number = TaskCase + " (запись 3)", Title = "Задание по объекту (вторая запись)", Kind = CaseKind.ObjectTask,
                OpenedAt = task.OpenedAt, DivisionId = task.DivisionId, Classification = task.Classification, Status = CaseStatus.InProgress,
                TaskNumber = task.TaskNumber, InitiatorUnitId = task.InitiatorUnitId, Justification = "обоснование", Purpose = "цель задания",
            });
            await db.SaveChangesAsync();
        }

        await SeedReceiverAsync(withDivision: true);
        var package = await ExportAsync(world.Period);
        var (service, channel, _) = BuildReceiver();

        var import = await ImportAsync(service, channel, package);

        import.Status.ShouldBe(ExchangeImportStatus.Succeeded, import.Error);
        (import.CasesCreated, import.Conflicts).ShouldBe((3, 0));
        await using var rx = _rxInvestigation.CreateDbContext();
        (await rx.Cases.CountAsync(c => c.TaskNumber == "З-77")).ShouldBe(2);
        (await rx.ExchangeImportConflicts.CountAsync()).ShouldBe(0);
    }

    [Fact(DisplayName = "ADR-0068 v0.2: у получателя нет подразделения источника по коду — отказ до записи с перечнем, сбой в журнале аудита")]
    public async Task Missing_division_is_refused_before_writing()
    {
        var world = await SeedSourceAsync();
        await SeedReceiverAsync(withDivision: false);
        var package = await ExportAsync(world.Period);
        var (service, channel, _) = BuildReceiver();

        var import = await ImportAsync(service, channel, package);

        import.Status.ShouldBe(ExchangeImportStatus.Failed);
        import.Error.ShouldNotBeNull().ShouldContain("код ОУ2");
        await using var db = _rxInvestigation.CreateDbContext();
        (await db.Cases.CountAsync()).ShouldBe(0);
        (await db.ExchangeSourceRecords.CountAsync()).ShouldBe(0);
        await using var core = _rxCore.CreateDbContext();
        var failure = await core.AuditRecords.SingleAsync(r => r.ObjectRef == "investigation:exchange:import:" + package);
        failure.Outcome.ShouldBe(AuditOutcome.Error);
    }

    [Fact(DisplayName = "ТБ-083: без отметки шлюза и с неверным паролем ключа приёма — отказ сразу, приём не начат")]
    public async Task Start_needs_gateway_and_password()
    {
        var world = await SeedSourceAsync();
        await SeedReceiverAsync(withDivision: true);
        var package = await ExportAsync(world.Period);
        var (service, _, _) = BuildReceiver();

        (await service.StartImportAsync(package, gatewayConfirmed: false, ReceiverAdminId, ReceivingPassword)).Error
            .ShouldNotBeNull().ShouldContain("шлюзе");
        (await service.StartImportAsync(package, gatewayConfirmed: true, ReceiverAdminId, "не тот пароль")).Error
            .ShouldNotBeNull().ShouldContain("Пароль ключа приёма");
        (await service.StartImportAsync("../" + package, gatewayConfirmed: true, ReceiverAdminId, ReceivingPassword)).Error
            .ShouldNotBeNull().ShouldContain("нет");

        await using var db = _rxInvestigation.CreateDbContext();
        (await db.ExchangeImports.CountAsync()).ShouldBe(0);
    }

    [Fact(DisplayName = "ТБ-083: чужая подпись — пакет в перечне с причиной; подменённый после выбора файл — отказ полной проверки, ничего не записано")]
    public async Task Foreign_or_tampered_package_is_refused()
    {
        var world = await SeedSourceAsync();
        await SeedReceiverAsync(withDivision: true);
        var package = await ExportAsync(world.Period);
        var (service, channel, _) = BuildReceiver();

        // Копия пакета, переподписанная чужим ключом: в перечне — «подпись не сходится».
        var forged = package.Replace("office-2", "office-9", StringComparison.Ordinal);
        CopyDirectory(Path.Combine(Medium, package), Path.Combine(Medium, forged));
        using (var stranger = RSA.Create(BackupKeys.MinKeyBits))
        {
            var manifest = await File.ReadAllBytesAsync(Path.Combine(Medium, forged, SealedPackageManifest.FileName));
            await File.WriteAllTextAsync(Path.Combine(Medium, forged, SealedPackageManifest.SignatureFileName),
                Convert.ToBase64String(SealedSignature.Sign(manifest, stranger)));
        }

        Directory.CreateDirectory(Path.Combine(Medium, ExchangePackageWriter.FolderPrefix + "office-2-2026-01-x" + SealedPackageWriter.PartialSuffix));
        var overview = await service.GetImportOverviewAsync();
        overview.Readiness.Ready.ShouldBeTrue(string.Join("; ", overview.Readiness.Problems));
        overview.Packages.Select(p => p.FolderName).ShouldBe([package, forged], ignoreOrder: true);
        overview.Packages.Single(p => p.FolderName == package).CanImport.ShouldBeTrue();
        overview.Packages.Single(p => p.FolderName == forged).Problem.ShouldNotBeNull().ShouldContain("Подпись манифеста не сходится");

        // Пакет выбран и запущен, а файл подменили до полной проверки — приём отказывает, записей нет.
        var started = await service.StartImportAsync(package, gatewayConfirmed: true, ReceiverAdminId, ReceivingPassword);
        started.Error.ShouldBeNull();
        var media = Directory.EnumerateFiles(Path.Combine(Medium, package, "media")).First();
        var bytes = await File.ReadAllBytesAsync(media);
        bytes[^10] ^= 0x01;
        await File.WriteAllBytesAsync(media, bytes);
        await using (var jobs = channel.ReadAllAsync(CancellationToken.None).GetAsyncEnumerator())
        {
            (await jobs.MoveNextAsync()).ShouldBeTrue();
            await service.RunAsync(jobs.Current, CancellationToken.None);
        }

        await using var db = _rxInvestigation.CreateDbContext();
        var import = await db.ExchangeImports.SingleAsync();
        import.Status.ShouldBe(ExchangeImportStatus.Failed);
        import.Error.ShouldNotBeNull().ShouldContain("SHA-256");
        (await db.Cases.CountAsync()).ShouldBe(0);
    }

    // ---------- окружение ----------

    private (ExchangeImportService Service, ExchangeImportChannel Channel, RecordingQueue Queue) BuildReceiver()
    {
        var settings = new ExchangeSettings
        {
            InstanceId = "office-1",
            ImportFolder = Medium,
            ReceivingKeyPath = Path.Combine(Keys, "recipient-private.pem"),
            ReceivingKeySha256 = BackupKeys.FingerprintHex(_recipient),
            Sources = new Dictionary<string, ExchangeSourceKey>(StringComparer.Ordinal)
            {
                ["office-2"] = new(Path.Combine(Keys, "signing-public.pem"), BackupKeys.FingerprintHex(_signing)),
            },
        };
        var storage = new LocalFileStorage(ReceiverStorage);
        var channel = new ExchangeImportChannel();
        var queue = new RecordingQueue();
        var service = new ExchangeImportService(
            _rxInvestigation,
            settings,
            new ExchangePackages(),
            channel,
            new ExchangeRecordImporter(_rxInvestigation, new UserAccountStore(_rxCore), TimeProvider.System),
            new MediaExchangeSource(_rxMedia, storage),
            new MediaStore(_rxMedia, storage),
            InvestigationTestKit.CreateCaseScope(_rxInvestigation, _rxCore),
            MediaUploadOptions.Default,
            queue,
            new AuditWriter(_rxCore),
            TimeProvider.System,
            NullLogger<ExchangeImportService>.Instance);
        return (service, channel, queue);
    }

    private async Task<ExchangeImport> ImportAsync(ExchangeImportService service, ExchangeImportChannel channel, string package)
    {
        var started = await service.StartImportAsync(package, gatewayConfirmed: true, ReceiverAdminId, ReceivingPassword);
        started.Error.ShouldBeNull();
        await using (var jobs = channel.ReadAllAsync(CancellationToken.None).GetAsyncEnumerator())
        {
            (await jobs.MoveNextAsync()).ShouldBeTrue();
            await service.RunAsync(jobs.Current, CancellationToken.None);
        }

        await using var db = _rxInvestigation.CreateDbContext();
        return await db.ExchangeImports.AsNoTracking().SingleAsync(i => i.Id == started.ImportId);
    }

    private async Task<string> ExportAsync((int Year, int Month) period)
    {
        var settings = new ExchangeSettings
        {
            InstanceId = "office-2",
            TargetFolder = Medium,
            RecipientKeyPath = Path.Combine(Keys, "recipient-public.pem"),
            RecipientKeySha256 = BackupKeys.FingerprintHex(_recipient),
            SigningKeyPath = Path.Combine(Keys, "signing-private.pem"),
            SigningKeySha256 = BackupKeys.FingerprintHex(_signing),
        };
        var packages = new ExchangePackages();
        var channel = new ExchangeExportChannel();
        var writer = new ExchangePackageWriter(
            _investigation, new MediaExchangeSource(_media, new LocalFileStorage(SourceStorage)), new UserAccountStore(_core), packages);
        var service = new ExchangeExportService(
            _investigation, settings, channel, writer, packages, new AuditWriter(_core), TimeProvider.System,
            NullLogger<ExchangeExportService>.Instance);
        var started = await service.StartAsync(
            new ExchangeExportRequest(period.Year, period.Month, [], [nameof(MediaKind.Image), nameof(MediaKind.Video), nameof(MediaKind.Audio)]),
            SourceAdminId, SigningPassword);
        started.Error.ShouldBeNull();
        await using (var jobs = channel.ReadAllAsync(CancellationToken.None).GetAsyncEnumerator())
        {
            (await jobs.MoveNextAsync()).ShouldBeTrue();
            await service.RunAsync(jobs.Current, CancellationToken.None);
        }

        await using var db = _investigation.CreateDbContext();
        var export = await db.ExchangeExports.AsNoTracking().SingleAsync(e => e.Id == started.ExportId);
        export.Error.ShouldBeNull();
        return export.PackageName.ShouldNotBeNull();
    }

    private async Task<(int Cases, int Persons, int Phones, int Events, int Participants, int Reports, int Links, int Assets)> CountReceiverAsync()
    {
        await using var db = _rxInvestigation.CreateDbContext();
        await using var media = _rxMedia.CreateDbContext();
        return (await db.Cases.CountAsync(), await db.Persons.CountAsync(), await db.PersonPhones.CountAsync(),
            await db.LinkEvents.CountAsync(), await db.LinkEventParticipants.CountAsync(), await db.CaseReports.CountAsync(),
            await db.CaseMediaLinks.CountAsync(), await media.Assets.CountAsync());
    }

    private sealed record World((int Year, int Month) Period);

    // Офис №2: подразделение ОУ-2 (код ОУ2), справочник, сотрудник-эксперт; дело-разработка с объектом (телефон, адрес,
    // транспорт), связью и сводкой; задание по объекту с неустановленным лицом; событие связи с обоими; видео в первом
    // деле и фото во втором — файлы на диске; эталон объекта (ждёт этапа 3).
    private async Task<World> SeedSourceAsync()
    {
        int expertId;
        await using (var core = _core.CreateDbContext())
        {
            var expert = new ISC.AI.Persistence.Entities.AppUserEntity { UserName = "expert", DisplayName = "Экспертов Э.", IsActive = true };
            core.Users.Add(expert);
            await core.SaveChangesAsync();
            expertId = expert.Id;
        }

        int divisionId, targetCaseId, taskCaseId, targetId, unidentifiedId;
        await using (var db = _investigation.CreateDbContext())
        {
            var division = new Division { Name = "ОУ-2", Code = "ОУ2", IsActive = true, Direction = CaseDirection.Establishment };
            var unit = new ReferenceItem { Kind = ReferenceKind.InitiatorUnit, Name = "ГУ уголовного розыска", IsActive = true, SortOrder = 1 };
            var brother = new ReferenceItem { Kind = ReferenceKind.LinkType, Name = "брат", IsActive = true, SortOrder = 1 };
            db.Divisions.Add(division);
            db.ReferenceItems.AddRange(unit, brother);
            await db.SaveChangesAsync();
            divisionId = division.Id;

            var targetCase = new CaseFile
            {
                Number = TargetCase, Title = "Разработка", Kind = CaseKind.OperativeMeasure, OpenedAt = new DateOnly(2026, 1, 10),
                DivisionId = divisionId, Classification = ClassificationLevels.ForOfficialUse, Status = CaseStatus.InProgress,
                InvestigatorUserId = expertId,
            };
            var taskCase = new CaseFile
            {
                Number = TaskCase, Title = "Задание по объекту", Kind = CaseKind.ObjectTask, OpenedAt = new DateOnly(2026, 2, 3),
                DivisionId = divisionId, Classification = ClassificationLevels.Secret, Status = CaseStatus.InProgress,
                TaskNumber = "З-77", InitiatorUnitId = unit.Id, Justification = "обоснование", Purpose = "цель задания",
            };
            db.Cases.AddRange(targetCase, taskCase);
            await db.SaveChangesAsync();
            (targetCaseId, taskCaseId) = (targetCase.Id, taskCase.Id);

            var target = new Person
            {
                CaseId = targetCaseId, DisplayName = "Иванов Иван", Role = PersonRole.Target, Classification = ClassificationLevels.Secret,
                DivisionId = divisionId,
            };
            var unidentified = new Person
            {
                CaseId = taskCaseId, DisplayName = "Неустановленное лицо № 1", IsUnidentified = true, UnidentifiedNumber = 1,
                Role = PersonRole.Target, Classification = ClassificationLevels.Secret, DivisionId = divisionId,
            };
            db.Persons.AddRange(target, unidentified);
            await db.SaveChangesAsync();
            (targetId, unidentifiedId) = (target.Id, unidentified.Id);

            db.Persons.Add(new Person
            {
                CaseId = targetCaseId, DisplayName = "Сидоров Семён", Role = PersonRole.Link, LinkedToPersonId = targetId, LinkTypeId = brother.Id,
                Classification = ClassificationLevels.ForOfficialUse, DivisionId = divisionId,
            });
            db.PersonPhones.Add(new PersonPhone
            {
                PersonId = targetId, Kind = PhoneKind.Mobile, Number = "0555 123 456", NumberNormalized = RequisiteNormalizer.Phone("0555 123 456")!,
                Classification = ClassificationLevels.Secret, DivisionId = divisionId,
            });
            db.PersonAddresses.Add(new PersonAddress
            {
                PersonId = targetId, Kind = AddressKind.Residence, Text = "г. Бишкек, ул. Ленина, 15",
                TextNormalized = RequisiteNormalizer.Address("г. Бишкек, ул. Ленина, 15")!, Classification = ClassificationLevels.Secret,
                DivisionId = divisionId,
            });
            db.PersonVehicles.Add(new PersonVehicle
            {
                PersonId = targetId, PlateNumber = "01KG123ABC", PlateNormalized = RequisiteNormalizer.Plate("01KG123ABC"), Make = "Toyota",
                Classification = ClassificationLevels.Secret, DivisionId = divisionId,
            });
            var linkEvent = new LinkEvent
            {
                Kind = LinkEventKind.Meeting, Title = "Встреча у рынка", OccurredOn = new DateOnly(2026, 3, 2),
                Classification = ClassificationLevels.ForOfficialUse, DivisionId = divisionId,
            };
            db.LinkEvents.Add(linkEvent);
            await db.SaveChangesAsync();
            db.LinkEventParticipants.AddRange(
                new LinkEventParticipant
                {
                    EventId = linkEvent.Id, PersonId = targetId, Level = LinkLevel.Confirmed,
                    Classification = ClassificationLevels.ForOfficialUse, DivisionId = divisionId,
                },
                new LinkEventParticipant
                {
                    EventId = linkEvent.Id, PersonId = unidentifiedId, Level = LinkLevel.Probable,
                    Classification = ClassificationLevels.ForOfficialUse, DivisionId = divisionId,
                });
            var content = new CaseReportContent(Conclusion: "Встреча подтверждена наружным наблюдением.");
            var report = new CaseReport
            {
                CaseId = targetCaseId, PersonId = targetId, Kind = CaseReportKind.SummaryOn, ReportDate = new DateOnly(2026, 3, 3),
                IsActive = true, CurrentRevision = 1, SearchText = content.SearchText(CaseReportKind.SummaryOn),
                Classification = ClassificationLevels.ForOfficialUse, DivisionId = divisionId,
            };
            db.CaseReports.Add(report);
            db.CaseReportRevisions.Add(new CaseReportRevision
            {
                Report = report, Number = 1, ContentJson = content.ToJson(),
                Classification = ClassificationLevels.ForOfficialUse, DivisionId = divisionId,
            });
            await db.SaveChangesAsync();
        }

        var video = await StoreAssetAsync(_media, SourceStorage, VideoBytes, MediaKind.Video, "video/mp4", ClassificationLevels.ForOfficialUse, divisionId);
        var photo = await StoreAssetAsync(_media, SourceStorage, PhotoBytes, MediaKind.Image, "image/jpeg", ClassificationLevels.Secret, divisionId);
        await using (var db = _investigation.CreateDbContext())
        {
            db.CaseMediaLinks.AddRange(
                new CaseMediaLink { CaseId = targetCaseId, MediaAssetId = video, Place = "ул. Ленина, 15", LinkedByUserId = expertId },
                new CaseMediaLink { CaseId = taskCaseId, MediaAssetId = photo, LinkedByUserId = expertId });
            db.ReferencePhotos.Add(new ReferencePhoto
            {
                PersonId = targetId, MediaAssetId = photo, Classification = ClassificationLevels.Secret, DivisionId = divisionId,
            });
            await db.SaveChangesAsync();

            var created = await db.Cases.Where(c => c.Id == targetCaseId).Select(c => c.CreatedAt).SingleAsync();
            var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(created, DateTimeKind.Utc), TimeZoneInfo.Local);
            return new World((local.Year, local.Month));
        }
    }

    // Офис №1: своё подразделение ОУ-2 с тем же кодом (или без него), сотрудник с тем же именем входа — другой номер учётки.
    private async Task<int> SeedReceiverAsync(bool withDivision)
    {
        await using (var core = _rxCore.CreateDbContext())
        {
            core.Users.Add(new ISC.AI.Persistence.Entities.AppUserEntity { UserName = "admin-office-1", DisplayName = "Администратор", IsActive = true });
            await core.SaveChangesAsync();
            var expert = new ISC.AI.Persistence.Entities.AppUserEntity { UserName = "EXPERT", DisplayName = "Экспертов Э.", IsActive = true };
            core.Users.Add(expert);
            await core.SaveChangesAsync();

            await using var db = _rxInvestigation.CreateDbContext();
            db.Divisions.Add(new Division { Name = "ОН", Code = "ОН", IsActive = true, Direction = CaseDirection.Surveillance });
            if (withDivision)
            {
                db.Divisions.Add(new Division { Name = "ОУ-2 (Офис №2)", Code = "оу2", IsActive = true, Direction = CaseDirection.Establishment });
            }

            await db.SaveChangesAsync();
            return expert.Id;
        }
    }

    private static async Task<int> StoreAssetAsync(
        MediaContextFactory factory, string storage, byte[] content, MediaKind kind, string contentType, short classification, int divisionId)
    {
        await using var media = factory.CreateDbContext();
        var asset = new MediaAsset
        {
            Kind = kind, OriginalFileName = kind == MediaKind.Video ? "запись.mp4" : "снимок.jpg",
            StoredFileName = Guid.NewGuid().ToString("N") + (kind == MediaKind.Video ? ".mp4" : ".jpg"), ContentType = contentType,
            ContentHash = Hash(content), ByteSize = content.Length, Classification = classification, DivisionId = divisionId,
            IndexStatus = MediaIndexStatus.Indexed,
        };
        media.Assets.Add(asset);
        await media.SaveChangesAsync();
        var folder = Path.Combine(storage, MediaFileCategories.Originals, asset.Id.ToString(CultureInfo.InvariantCulture));
        Directory.CreateDirectory(folder);
        await File.WriteAllBytesAsync(Path.Combine(folder, asset.StoredFileName), content);
        return asset.Id;
    }

    private static string Hash(byte[] content) => Convert.ToHexString(SHA256.HashData(content));

    private static void CopyDirectory(string from, string to)
    {
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }

    private sealed class RecordingQueue : IBackgroundTaskQueue
    {
        public List<string> Kinds { get; } = [];

        public ValueTask<Guid> EnqueueAsync(
            string kind, Func<IServiceProvider, CancellationToken, Task> work, CancellationToken cancellationToken = default)
        {
            Kinds.Add(kind);
            return ValueTask.FromResult(Guid.NewGuid());
        }
    }
}
