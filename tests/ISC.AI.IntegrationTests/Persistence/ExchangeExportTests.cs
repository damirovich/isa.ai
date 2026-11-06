using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using ISC.AI.Backup;
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
using ISC.AI.Profile.Investigation.Domain.Exchange;
using ISC.AI.Profile.Investigation.Domain.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Testcontainers.PostgreSql;

namespace ISC.AI.IntegrationTests.Persistence;

/// <summary>
/// Пакет обмена между офисами, сторона источника (ТФ-ОБМ-01, ТБ-083, ТО-инф-14, ADR-0068), на настоящей БД: схемы
/// <c>core</c>, <c>media</c> и <c>investigation</c>, настоящие файлы носителей и ключи RSA. Проверяется: отбор дел с движением
/// за месяц, состав пакета, отсутствие шаблонов лиц (ТБ-076), подпись манифеста и хеши файлов, расшифровка ключом
/// получателя, неповторение ушедших файлов, отказ на неверный пароль, акт в журнале аудита, уборка прерванной выгрузки.
/// </summary>
/// <remarks>Требуется Docker. Ключи — 3072 бита (наименьшие допустимые): тесты быстрее.</remarks>
public sealed class ExchangeExportTests : IAsyncLifetime
{
    private const string Password = "пароль-хранителя-ключа-подписи";
    private const string CaseNumber = "ОУ-2/17-2026";
    private const int AdminId = 1203;

    private static readonly byte[] VideoBytes = Encoding.UTF8.GetBytes(new string('в', 3000) + " — содержимое видеозаписи");

    private readonly PostgreSqlContainer _postgres = TestPostgres.Create();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "iscai-exchange-tests", Guid.NewGuid().ToString("N"));

    private CoreContextFactory _core = null!;
    private MediaContextFactory _media = null!;
    private InvestigationContextFactory _investigation = null!;
    private RSA _recipient = null!;
    private RSA _signing = null!;
    private int _expertId;
    private int _verifierId;

    private string Storage => Path.Combine(_root, "storage");

    private string Medium => Path.Combine(_root, "medium");

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        var connection = _postgres.GetConnectionString();
        _core = new CoreContextFactory(connection);
        _media = new MediaContextFactory(connection);
        _investigation = new InvestigationContextFactory(connection);
        await using (var db = _core.CreateDbContext())
        {
            await db.Database.MigrateAsync();
        }

        await using (var db = _media.CreateDbContext())
        {
            await db.Database.MigrateAsync();
        }

        await InvestigationTestKit.MigrateAsync(_investigation);
        Directory.CreateDirectory(Storage);
        Directory.CreateDirectory(Medium);
        _recipient = RSA.Create(BackupKeys.MinKeyBits);
        _signing = RSA.Create(BackupKeys.MinKeyBits);
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

    [Fact(DisplayName = "ТФ-ОБМ-01: пакет за месяц — дело, объект, носитель с файлом, лицо, появление с решениями; подпись и хеши верны, содержимое расшифровывается ключом получателя, шаблонов лиц нет")]
    public async Task Package_carries_the_month_and_verifies()
    {
        var world = await SeedWorldAsync();
        var (service, channel) = Build();

        var export = await ExportAsync(service, channel, world.Period, resend: false);

        export.Status.ShouldBe(ExchangeExportStatus.Succeeded);
        (export.Cases, export.Persons, export.Assets, export.Files, export.Appearances).ShouldBe((1, 2, 1, 1, 1));
        export.Classification.ShouldBe(ClassificationLevels.Secret);
        var folder = Path.Combine(Medium, export.PackageName.ShouldNotBeNull());
        Directory.Exists(folder + SealedPackageWriter.PartialSuffix).ShouldBeFalse();

        // Проверка без ключа получателя — подпись источника, состав, хеши, сигнатуры (то, что сделает шлюз и импорт).
        var check = await SealedPackageVerifier.VerifyAsync(folder, _signing, SealedFormat.Exchange, BackupKeys.FingerprintHex(_recipient));
        check.Problems.ShouldBeEmpty();
        check.Ok.ShouldBeTrue();
        var manifest = check.Manifest!;
        manifest.SourceInstance.ShouldBe("office-2");
        manifest.Marking.ShouldBe(ClassificationLevels.Label(ClassificationLevels.Secret));
        manifest.Counts["cases"].ShouldBe(1);
        manifest.Counts["links"].ShouldBe(1);

        // Содержимое — ключом получателя.
        var json = await DecryptAsync(Path.Combine(folder, ExchangePackageData.FileName));
        var data = JsonSerializer.Deserialize<ExchangePackageData>(json, ExchangePackageData.Json).ShouldNotBeNull();
        data.Cases.ShouldHaveSingleItem().Number.ShouldBe(CaseNumber);
        data.Persons.Count.ShouldBe(2);
        data.Persons.Single(p => p.Role == PersonRole.Target).Phones.ShouldHaveSingleItem().Number.ShouldBe("0555 123 456");
        data.Persons.Single(p => p.Role == PersonRole.Link).LinkedToPersonSourceId.ShouldBe(world.TargetId);
        data.Divisions.ShouldHaveSingleItem().Code.ShouldBe("ОУ2");

        var asset = data.Assets.ShouldHaveSingleItem();
        asset.Kind.ShouldBe(nameof(MediaKind.Video));
        asset.File.ShouldBe("media/000001.enc");
        asset.Cases.ShouldHaveSingleItem().Place.ShouldBe("ул. Ленина, 15");
        (await DecryptAsync(Path.Combine(folder, "media", "000001.enc"))).ShouldBe(VideoBytes);

        var face = data.Faces.ShouldHaveSingleItem();
        (face.TimestampMs, face.BoxX, face.BoxWidth).ShouldBe((4000L, 10f, 80f));

        var appearance = data.Appearances.ShouldHaveSingleItem();
        appearance.PersonSourceId.ShouldBe(world.TargetId);
        appearance.Decisions.Select(d => (d.Stage, d.Verdict, d.UserSourceId))
            .ShouldBe([("Expert", "Confirmed", _expertId), ("Verifier", "Confirmed", _verifierId)]);
        data.Users.Select(u => u.UserName).ShouldBe(["expert", "verifier"], ignoreOrder: true);

        // ТБ-076: ни шаблонов, ни опорных точек в содержимом нет.
        var text = Encoding.UTF8.GetString(json);
        text.ShouldNotContain("embedding", Case.Insensitive);
        text.ShouldNotContain("landmarks", Case.Insensitive);

        // Акт экспорта — в журнале аудита с маркировкой пакета.
        await using var core = _core.CreateDbContext();
        var act = await core.AuditRecords.SingleAsync(r => r.ObjectRef == "investigation:exchange:package:" + export.PackageName);
        act.Action.ShouldBe(AuditAction.Export);
        act.Classification.ShouldBe(ClassificationLevels.Secret);
        act.SubjectId.ShouldBe(AdminId);
        act.PayloadSensitive.ShouldNotBeNull().ShouldContain(manifest.RecipientKeyFingerprint);
        act.PayloadSensitive.ShouldNotContain(Password);
    }

    [Fact(DisplayName = "ADR-0068: повторный пакет за тот же месяц не повторяет ушедший файл — носитель только реквизитами; «отправить повторно» — повторяет")]
    public async Task Shipped_files_are_not_repeated()
    {
        var world = await SeedWorldAsync();
        var (service, channel) = Build();

        (await ExportAsync(service, channel, world.Period, resend: false)).Files.ShouldBe(1);

        var second = await ExportAsync(service, channel, world.Period, resend: false);
        (second.Assets, second.Files).ShouldBe((1, 0));
        var data = JsonSerializer.Deserialize<ExchangePackageData>(
            await DecryptAsync(Path.Combine(Medium, second.PackageName!, ExchangePackageData.FileName)), ExchangePackageData.Json)!;
        data.Assets.ShouldHaveSingleItem().File.ShouldBeNull();
        Directory.Exists(Path.Combine(Medium, second.PackageName!, ExchangePackageData.MediaFolder)).ShouldBeFalse();

        (await ExportAsync(service, channel, world.Period, resend: true)).Files.ShouldBe(1);
    }

    [Fact(DisplayName = "ТБ-083: неверный пароль ключа подписи — отказ сразу, выгрузка не начата")]
    public async Task Wrong_password_is_refused_at_start()
    {
        var world = await SeedWorldAsync();
        var (service, _) = Build();

        var started = await service.StartAsync(Request(world.Period, resend: false), AdminId, "не тот пароль");

        started.ExportId.ShouldBeNull();
        started.Error.ShouldNotBeNull().ShouldContain("Пароль");
        await using var db = _investigation.CreateDbContext();
        (await db.ExchangeExports.CountAsync()).ShouldBe(0);
    }

    [Fact(DisplayName = "ТБ-083: подменённый файл, лишний файл и чужой ключ подписи обнаруживаются проверкой пакета")]
    public async Task Tampering_is_detected()
    {
        var world = await SeedWorldAsync();
        var (service, channel) = Build();
        var folder = Path.Combine(Medium, (await ExportAsync(service, channel, world.Period, resend: false)).PackageName!);

        using (var stranger = RSA.Create(BackupKeys.MinKeyBits))
        {
            var foreign = await SealedPackageVerifier.VerifyAsync(folder, stranger, SealedFormat.Exchange);
            foreign.Ok.ShouldBeFalse();
            foreign.Manifest.ShouldBeNull();
        }

        var media = Path.Combine(folder, "media", "000001.enc");
        var bytes = await File.ReadAllBytesAsync(media);
        bytes[^20] ^= 0x01;
        await File.WriteAllBytesAsync(media, bytes);
        await File.WriteAllTextAsync(Path.Combine(folder, "подброшено.txt"), "x");

        var check = await SealedPackageVerifier.VerifyAsync(folder, _signing, SealedFormat.Exchange);
        check.Ok.ShouldBeFalse();
        check.Problems.ShouldContain(p => p.Contains("media/000001.enc") && p.Contains("SHA-256"));
        check.Problems.ShouldContain(p => p.Contains("Лишний файл"));
    }

    [Fact(DisplayName = "ADR-0068: после перезапуска хоста незавершённая выгрузка — «прервана», её каталог «.partial» удалён")]
    public async Task Interrupted_export_is_cleaned_up()
    {
        var (service, _) = Build();
        await using (var db = _investigation.CreateDbContext())
        {
            db.ExchangeExports.Add(new ExchangeExport
            {
                StartedAt = DateTime.UtcNow, Status = ExchangeExportStatus.Running, StartedByUserId = AdminId,
                Year = 2026, Month = 10, MediaKinds = "Image",
            });
            await db.SaveChangesAsync();
        }

        var partial = Path.Combine(Medium, ExchangePackageWriter.FolderPrefix + "office-2-2026-10-x" + SealedPackageWriter.PartialSuffix);
        Directory.CreateDirectory(partial);

        (await service.RecoverAfterRestartAsync()).ShouldBe(1);
        Directory.Exists(partial).ShouldBeFalse();
        await using var read = _investigation.CreateDbContext();
        (await read.ExchangeExports.SingleAsync()).Status.ShouldBe(ExchangeExportStatus.Interrupted);
    }

    // ---------- окружение ----------

    private (ExchangeExportService Service, ExchangeExportChannel Channel) Build()
    {
        var keys = Path.Combine(_root, "keys");
        Directory.CreateDirectory(keys);
        var recipientPath = Path.Combine(keys, "recipient-public.pem");
        var signingPath = Path.Combine(keys, "signing-private.pem");
        File.WriteAllText(recipientPath, BackupKeys.ExportPublicPem(_recipient));
        File.WriteAllText(signingPath, BackupKeys.ExportPrivatePem(_signing, Password));

        var settings = new ExchangeSettings
        {
            InstanceId = "office-2",
            TargetFolder = Medium,
            RecipientKeyPath = recipientPath,
            RecipientKeySha256 = BackupKeys.FingerprintHex(_recipient),
            SigningKeyPath = signingPath,
            SigningKeySha256 = BackupKeys.FingerprintHex(_signing),
        };
        var packages = new ExchangePackages();
        settings.Check(packages).Problems.ShouldBeEmpty();

        var channel = new ExchangeExportChannel();
        var writer = new ExchangePackageWriter(
            _investigation, new MediaExchangeSource(_media, new LocalFileStorage(Storage)), new UserAccountStore(_core), packages);
        var service = new ExchangeExportService(
            _investigation, settings, channel, writer, packages, new AuditWriter(_core), TimeProvider.System,
            NullLogger<ExchangeExportService>.Instance);
        return (service, channel);
    }

    private static ExchangeExportRequest Request((int Year, int Month) period, bool resend) =>
        new(period.Year, period.Month, [], [nameof(MediaKind.Image), nameof(MediaKind.Video), nameof(MediaKind.Audio)], resend);

    private async Task<ExchangeExport> ExportAsync(
        ExchangeExportService service, ExchangeExportChannel channel, (int Year, int Month) period, bool resend)
    {
        var started = await service.StartAsync(Request(period, resend), AdminId, Password);
        started.Error.ShouldBeNull();
        await using (var jobs = channel.ReadAllAsync(CancellationToken.None).GetAsyncEnumerator())
        {
            (await jobs.MoveNextAsync()).ShouldBeTrue();
            await service.RunAsync(jobs.Current, CancellationToken.None);
        }

        // Каталоги пакетов называются по секундам: следующий пакет — в следующую секунду.
        await Task.Delay(1100);
        await using var db = _investigation.CreateDbContext();
        var export = await db.ExchangeExports.AsNoTracking().SingleAsync(e => e.Id == started.ExportId);
        export.Error.ShouldBeNull();
        return export;
    }

    private async Task<byte[]> DecryptAsync(string path)
    {
        await using var input = File.OpenRead(path);
        using var output = new MemoryStream();
        await SealedFile.DecryptAsync(input, output, _recipient, SealedFormat.Exchange);
        return output.ToArray();
    }

    private sealed record World(int TargetId, (int Year, int Month) Period);

    // Дело ОУ-2 с объектом и связью, телефон, видео с лицом (файл на диске), кандидат с решениями эксперта и верификатора,
    // появление; сотрудники-эксперт и верификатор — в реестре ядра (их имена едут в пакете).
    private async Task<World> SeedWorldAsync()
    {
        await using (var core = _core.CreateDbContext())
        {
            var expert = new ISC.AI.Persistence.Entities.AppUserEntity { UserName = "expert", DisplayName = "Экспертов Э.", IsActive = true };
            var verifier = new ISC.AI.Persistence.Entities.AppUserEntity { UserName = "verifier", DisplayName = "Верификаторов В.", IsActive = true };
            core.Users.AddRange(expert, verifier);
            await core.SaveChangesAsync();
            (_expertId, _verifierId) = (expert.Id, verifier.Id);
        }

        int caseId, targetId, divisionId;
        await using (var db = _investigation.CreateDbContext())
        {
            var division = new Division { Name = "ОУ-2", Code = "ОУ2", IsActive = true, Direction = CaseDirection.Establishment };
            db.Divisions.Add(division);
            await db.SaveChangesAsync();
            divisionId = division.Id;

            var caseFile = new CaseFile
            {
                Number = CaseNumber, Title = "Разработка", Kind = CaseKind.OperativeMeasure, OpenedAt = new DateOnly(2026, 1, 10),
                DivisionId = divisionId, Classification = ClassificationLevels.ForOfficialUse, Status = CaseStatus.InProgress,
            };
            db.Cases.Add(caseFile);
            await db.SaveChangesAsync();
            caseId = caseFile.Id;

            var target = new Person
            {
                CaseId = caseId, DisplayName = "Иванов Иван", Role = PersonRole.Target,
                Classification = ClassificationLevels.Secret, DivisionId = divisionId,
            };
            db.Persons.Add(target);
            await db.SaveChangesAsync();
            targetId = target.Id;

            db.Persons.Add(new Person
            {
                CaseId = caseId, DisplayName = "Сидоров Семён", Role = PersonRole.Link, LinkedToPersonId = targetId,
                Classification = ClassificationLevels.ForOfficialUse, DivisionId = divisionId,
            });
            db.PersonPhones.Add(new PersonPhone
            {
                PersonId = targetId, Kind = PhoneKind.Mobile, Number = "0555 123 456", NumberNormalized = "0555123456",
                Classification = ClassificationLevels.ForOfficialUse, DivisionId = divisionId,
            });
            await db.SaveChangesAsync();
        }

        int assetId, faceId, sessionId, candidateId;
        await using (var media = _media.CreateDbContext())
        {
            var asset = new MediaAsset
            {
                Kind = MediaKind.Video, OriginalFileName = "запись.mp4", StoredFileName = Guid.NewGuid().ToString("N") + ".mp4",
                ContentType = "video/mp4", ContentHash = Convert.ToHexString(SHA256.HashData(VideoBytes)), ByteSize = VideoBytes.Length,
                DurationMs = 10_000, Classification = ClassificationLevels.ForOfficialUse, DivisionId = divisionId,
                IndexStatus = MediaIndexStatus.Indexed,
            };
            media.Assets.Add(asset);
            await media.SaveChangesAsync();
            assetId = asset.Id;

            var folder = Path.Combine(Storage, MediaFileCategories.Originals, assetId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Directory.CreateDirectory(folder);
            await File.WriteAllBytesAsync(Path.Combine(folder, asset.StoredFileName), VideoBytes);

            var frame = new MediaFrame { AssetId = assetId, Index = 100, TimestampMs = 4000 };
            media.Frames.Add(frame);
            await media.SaveChangesAsync();
            var face = new Face
            {
                AssetId = assetId, FrameId = frame.Id, BoxX = 10, BoxY = 20, BoxWidth = 80, BoxHeight = 90, Landmarks = new float[10],
                DetectionScore = 0.95f, QualityScore = 0.9f, QualityAcceptable = true,
                Classification = ClassificationLevels.ForOfficialUse, DivisionId = divisionId,
            };
            media.Faces.Add(face);
            await media.SaveChangesAsync();
            faceId = face.Id;

            var session = new SearchSession
            {
                CaseId = caseId, AuthorizationRef = "Поручение № 3", Scope = SearchScopeKind.CurrentCase, CaseIds = [caseId],
                ProbeSha256 = Guid.NewGuid().ToString("N"), TopK = 20, DetectorVersion = "yunet-test", EmbedderVersion = "sface-test",
                HnswEfSearch = 200, Classification = ClassificationLevels.ForOfficialUse, DivisionId = divisionId,
            };
            media.SearchSessions.Add(session);
            await media.SaveChangesAsync();
            sessionId = session.Id;

            var candidate = new SearchCandidate
            {
                SessionId = sessionId, Rank = 1, FaceId = faceId, AssetId = assetId, CosineDistance = 0.12, ModelVersion = "sface-test",
                Classification = ClassificationLevels.ForOfficialUse, DivisionId = divisionId,
            };
            media.SearchCandidates.Add(candidate);
            await media.SaveChangesAsync();
            candidateId = candidate.Id;

            media.VerificationDecisions.AddRange(
                new VerificationDecisionEntity
                {
                    CandidateId = candidateId, SubjectId = _expertId, Stage = VerificationStage.Expert,
                    Verdict = VerificationVerdict.Confirmed, Rationale = "совпадают черты лица", DecidedAtUtc = DateTime.UtcNow.AddMinutes(-10),
                },
                new VerificationDecisionEntity
                {
                    CandidateId = candidateId, SubjectId = _verifierId, Stage = VerificationStage.Verifier,
                    Verdict = VerificationVerdict.Confirmed, Rationale = "подтверждаю", DecidedAtUtc = DateTime.UtcNow.AddMinutes(-5),
                });
            await media.SaveChangesAsync();
        }

        await using (var db = _investigation.CreateDbContext())
        {
            db.CaseMediaLinks.Add(new CaseMediaLink { CaseId = caseId, MediaAssetId = assetId, Place = "ул. Ленина, 15", LinkedByUserId = AdminId });
            db.Appearances.Add(new Appearance
            {
                PersonId = targetId, CaseId = caseId, MediaAssetId = assetId, MediaFaceId = faceId, FrameIndex = 100,
                FrameTimestampMs = 4000, SearchSessionId = sessionId, CandidateId = candidateId, Similarity = 0.88,
                Status = AppearanceStatus.InvestigativeLead, ConfirmedAtUtc = DateTime.UtcNow, ExpertUserId = _expertId,
                VerifierUserId = _verifierId, Classification = ClassificationLevels.ForOfficialUse, DivisionId = divisionId,
            });
            await db.SaveChangesAsync();

            // Период — месяц, в котором засеян «мир» (по местному времени сервера, как у сборщика).
            var created = await db.Cases.Where(c => c.Id == caseId).Select(c => c.CreatedAt).SingleAsync();
            var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(created, DateTimeKind.Utc), TimeZoneInfo.Local);
            return new World(targetId, (local.Year, local.Month));
        }
    }
}
