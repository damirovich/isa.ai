using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using ISC.AI.Abstractions.Security;
using ISC.AI.Backup;
using ISC.AI.Modules.Media.Domain.Model;
using ISC.AI.Profile.Investigation.Application.Features.Exchange;
using ISC.AI.Profile.Investigation.Data.Exchange;
using ISC.AI.Profile.Investigation.Domain.Enums;
using ISC.AI.Profile.Investigation.Domain.Services;
using Microsoft.Extensions.Configuration;
using NSubstitute;
using Shouldly;
using Xunit;

namespace ISC.AI.UnitTests.Investigation;

/// <summary>
/// Обмен между офисами, сторона источника, без базы (ТФ-ОБМ-01, ТБ-083, ТФ-АДМ-05, ADR-0068): готовность экземпляра и
/// закрепление ключей по отпечатку, пароль ключа подписи — ни в тексте запроса, ни в журнале, проверка отбора, право
/// «Обмен между офисами» на сервере, период месяца по местному времени и имя каталога пакета.
/// </summary>
public sealed class OfficeExchangeTests : IDisposable
{
    private const string Password = "пароль-хранителя";
    private const int Me = 41;

    // Ключи 3072 бит — наименьшие допустимые: тесты быстрее.
    private static readonly Lazy<RSA> Recipient = new(() => RSA.Create(BackupKeys.MinKeyBits));
    private static readonly Lazy<RSA> Signing = new(() => RSA.Create(BackupKeys.MinKeyBits));

    // Ключи читает и сверяет с отпечатками ядро — настоящий порт, не подделка: проверяется вся цепочка.
    private static readonly ExchangePackages Packages = new();

    private static readonly string[] ExpectedCases = ["ОУ-2/17-2026"];
    private static readonly string[] ExpectedKinds = [nameof(MediaKind.Image), nameof(MediaKind.Video)];

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "iscai-exchange-unit", Guid.NewGuid().ToString("N"));
    private readonly IUserRoleStore _roles = Substitute.For<IUserRoleStore>();
    private readonly ISubjectProvider _subject = Substitute.For<ISubjectProvider>();
    private readonly IExchangeExport _exchange = Substitute.For<IExchangeExport>();

    public OfficeExchangeTests()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "medium"));
        _subject.GetCurrentUserIdAsync(Arg.Any<CancellationToken>()).Returns((int?)Me);
        _roles.AnyAdministratorAsync(Arg.Any<CancellationToken>()).Returns(true);
        _roles.ListAsync(Arg.Any<CancellationToken>()).Returns([]);
        _roles.ListPermissionOverridesAsync(Arg.Any<CancellationToken>()).Returns([]);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // Временный каталог — не повод валить прогон.
        }
    }

    [Fact(DisplayName = "Готовность: настроено всё — без замечаний, отпечатки ключей видны; ничего не настроено — замечание по каждому ключу настройки")]
    public void Readiness_lists_every_missing_setting()
    {
        var ready = Settings().Check(Packages);
        ready.Ready.ShouldBeTrue(string.Join("; ", ready.Problems));
        ready.RecipientKeyFingerprint.ShouldBe(BackupKeys.FingerprintHex(Recipient.Value));
        ready.SigningKeyFingerprint.ShouldBe(BackupKeys.FingerprintHex(Signing.Value));

        var empty = ExchangeSettings.Read(new ConfigurationBuilder().Build()).Check(Packages);
        empty.Ready.ShouldBeFalse();
        foreach (var key in new[] { "Exchange:InstanceId", "Exchange:TargetFolder", "Exchange:Recipient:PublicKey:Path",
                                    "Exchange:Signing:PrivateKey:Path", "Exchange:Signing:PrivateKey:Sha256" })
        {
            empty.Problems.ShouldContain(p => p.Contains(key), key);
        }
    }

    [Fact(DisplayName = "Настройка читается из конфигурации; пробелы — как «не задано»; номер экземпляра — только строчная латиница, цифры и дефис")]
    public void Settings_are_read_and_checked()
    {
        var configured = Settings();
        var read = ExchangeSettings.Read(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Exchange:InstanceId"] = " office-2 ",
            ["Exchange:TargetFolder"] = configured.TargetFolder,
            ["Exchange:Recipient:PublicKey:Path"] = configured.RecipientKeyPath,
            ["Exchange:Recipient:PublicKey:Sha256"] = configured.RecipientKeySha256,
            ["Exchange:Signing:PrivateKey:Path"] = configured.SigningKeyPath,
            ["Exchange:Signing:PrivateKey:Sha256"] = "   ",
        }).Build());

        read.InstanceId.ShouldBe("office-2");
        read.SigningKeySha256.ShouldBeNull();
        read.Check(Packages).Problems.ShouldHaveSingleItem().ShouldContain("Exchange:Signing:PrivateKey:Sha256");

        Settings(instanceId: "Офис 2").Check(Packages).Problems.ShouldHaveSingleItem().ShouldContain("Номер экземпляра");
        Settings(instanceId: new string('a', 33)).Check(Packages).Ready.ShouldBeFalse();
        Settings(targetFolder: Path.Combine(_dir, "нет-носителя")).Check(Packages).Problems
            .ShouldHaveSingleItem().ShouldContain("не подключён");
    }

    [Fact(DisplayName = "ТБ-083: ключ получателя не совпал с закреплённым отпечатком или файл не ключ — замечание, а не падение экрана")]
    public void Recipient_key_is_pinned()
    {
        var wrongPin = Settings(recipientSha256: BackupKeys.FingerprintHex(Signing.Value));
        wrongPin.Check(Packages).Problems.ShouldHaveSingleItem().ShouldContain("не совпал с закреплённым отпечатком");
        Should.Throw<InvalidOperationException>(() => wrongPin.LoadRecipientKey(Packages));

        var garbage = Path.Combine(_dir, "garbage.pem");
        File.WriteAllText(garbage, "-----BEGIN PUBLIC KEY-----\nэто не ключ\n-----END PUBLIC KEY-----\n");
        Settings(recipientPath: garbage).Check(Packages).Ready.ShouldBeFalse();
        File.WriteAllText(garbage, "просто текст");
        Settings(recipientPath: garbage).Check(Packages).Problems.ShouldHaveSingleItem().ShouldContain("нет открытого ключа RSA");
    }

    [Fact(DisplayName = "ТБ-083: ключ подписи — только с паролем хранителя и с закреплённым отпечатком")]
    public void Signing_key_needs_password_and_pin()
    {
        using (var key = Settings().LoadSigningKey(Packages, Password))
        {
            BackupKeys.FingerprintHex(key).ShouldBe(BackupKeys.FingerprintHex(Signing.Value));
        }

        Should.Throw<InvalidOperationException>(() => Settings().LoadSigningKey(Packages, "не тот"))
            .Message.ShouldStartWith("Пароль ключа подписи неверен");
        Should.Throw<InvalidOperationException>(
                () => Settings(signingSha256: BackupKeys.FingerprintHex(Recipient.Value)).LoadSigningKey(Packages, Password))
            .Message.ShouldContain("не совпал с закреплённым отпечатком");
        Should.Throw<InvalidOperationException>(() => Settings(signingPath: Path.Combine(_dir, "нет.pem")).LoadSigningKey(Packages, Password));
    }

    [Fact(DisplayName = "ТБ-083: пароль ключа подписи не попадает ни в текст запроса, ни в сводку журнала; сводка — период, число дел, виды")]
    public void Password_stays_out_of_text_and_audit()
    {
        var command = Command(cases: "ОУ-2/17-2026, ОУ-2/18-2026", resend: true);

        command.ToString().ShouldNotContain(Password);
        command.ToString().ShouldContain("2026-10");
        command.AuditSummary.ShouldNotBeNull().ShouldNotContain(Password);
        command.AuditSummary.ShouldBe("investigation:exchange:export:2026-10;cases=2;kinds=Image,Video;resend");
    }

    [Theory(DisplayName = "Номера дел из поля: запятая, точка с запятой, новая строка; пробелы по краям и повторы — убираются")]
    [InlineData(null, new string[0])]
    [InlineData("  ", new string[0])]
    [InlineData("ОУ-2/17-2026", new[] { "ОУ-2/17-2026" })]
    [InlineData(" ОУ-2/17-2026 ;ОУ-2/18-2026\r\nОУ-2/17-2026,, ", new[] { "ОУ-2/17-2026", "ОУ-2/18-2026" })]
    public void Case_numbers_are_parsed(string? text, string[] expected) =>
        StartExchangeExportCommand.ParseCaseNumbers(text).ShouldBe(expected);

    [Fact(DisplayName = "Проверка отбора: верный отбор проходит; месяц вне 1–12, год вне 2000–2100, будущий месяц, нет видов, нет пароля, больше 500 дел — отказ")]
    public void Validator_rejects_bad_selection()
    {
        var validator = new StartExchangeExportValidator(new FixedTime(new DateTimeOffset(2026, 11, 6, 10, 0, 0, TimeSpan.Zero)));

        validator.Validate(Command()).IsValid.ShouldBeTrue();
        validator.Validate(Command() with { Year = 2026, Month = 11 }).IsValid.ShouldBeTrue("текущий месяц можно");
        validator.Validate(Command() with { Month = 13 }).IsValid.ShouldBeFalse();
        validator.Validate(Command() with { Year = 1999 }).IsValid.ShouldBeFalse();
        validator.Validate(Command() with { Month = 12 }).Errors.ShouldContain(e => e.ErrorMessage.Contains("не наступил"));
        validator.Validate(Command() with { MediaKinds = [] }).IsValid.ShouldBeFalse();
        validator.Validate(Command() with { SigningKeyPassword = "" }).IsValid.ShouldBeFalse();
        var many = string.Join(',', Enumerable.Range(1, StartExchangeExportValidator.MaxCaseNumbers + 1).Select(i => $"ОУ-2/{i}-2026"));
        validator.Validate(Command(cases: many)).IsValid.ShouldBeFalse();
    }

    [Fact(DisplayName = "Право «Обмен между офисами»: не Администратору — отказ, выгрузка не запускается; Администратору — запуск с разобранным отбором")]
    public async Task Start_requires_exchange_permission()
    {
        _roles.GetRoleAsync(Me, Arg.Any<CancellationToken>()).Returns(InvestigationRole.Head);
        var handler = new StartExchangeExportCommand.Handler(_roles, _subject, _exchange);

        (await handler.Handle(Command(), CancellationToken.None)).Status.ShouldBeFalse();
        (await new GetExchangeOverviewQuery.Handler(_roles, _subject, _exchange).Handle(new GetExchangeOverviewQuery(), CancellationToken.None))
            .Status.ShouldBeFalse();
        await _exchange.DidNotReceiveWithAnyArgs().StartAsync(default!, default, default!, default);
        await _exchange.DidNotReceiveWithAnyArgs().GetOverviewAsync(default);

        _roles.GetRoleAsync(Me, Arg.Any<CancellationToken>()).Returns(InvestigationRole.Administrator);
        _exchange.StartAsync(Arg.Any<ExchangeExportRequest>(), Me, Password, Arg.Any<CancellationToken>())
            .Returns(new ExchangeStartResult(5, null));

        var started = await handler.Handle(
            Command(cases: "ОУ-2/17-2026\nОУ-2/17-2026") with { MediaKinds = [MediaKind.Video, MediaKind.Image, MediaKind.Video] },
            CancellationToken.None);

        started.Status.ShouldBeTrue();
        started.Data.ShouldBe(5);
        await _exchange.Received(1).StartAsync(
            Arg.Is<ExchangeExportRequest>(r => r.Year == 2026 && r.Month == 10 && r.ResendFiles
                && r.CaseNumbers.SequenceEqual(ExpectedCases) && r.MediaKinds.SequenceEqual(ExpectedKinds)),
            Me, Password, Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "Период — месяц по местному времени сервера (UTC+6): полуинтервал UTC; имя каталога пакета — экземпляр, период, время")]
    public void Month_and_folder_name()
    {
        var bishkek = TimeZoneInfo.CreateCustomTimeZone("test+6", TimeSpan.FromHours(6), "UTC+6", "UTC+6");

        var (from, to) = ExchangePackageJob.MonthUtc(2026, 10, bishkek);

        from.ShouldBe(new DateTime(2026, 9, 30, 18, 0, 0, DateTimeKind.Utc));
        to.ShouldBe(new DateTime(2026, 10, 31, 18, 0, 0, DateTimeKind.Utc));
        ExchangePackageJob.MonthUtc(2026, 12, bishkek).ToUtc.ShouldBe(new DateTime(2026, 12, 31, 18, 0, 0, DateTimeKind.Utc));
        ExchangePackageWriter.FolderNameFor("office-2", 2026, 10, new DateTimeOffset(2026, 11, 1, 9, 5, 7, TimeSpan.FromHours(6)))
            .ShouldBe("isc-exchange-office-2-2026-10-2026-11-01_090507");
        SealedPackageWriter.IsSafeName(ExchangePackageWriter.FolderNameFor("office-2", 2026, 1, DateTimeOffset.UnixEpoch)).ShouldBeTrue();
    }

    // ---------- окружение ----------

    private static StartExchangeExportCommand Command(string? cases = null, bool resend = true) =>
        new(2026, 10, cases, [MediaKind.Image, MediaKind.Video], resend, Password);

    private ExchangeSettings Settings(
        string? instanceId = "office-2",
        string? targetFolder = null,
        string? recipientPath = null,
        string? recipientSha256 = null,
        string? signingPath = null,
        string? signingSha256 = null)
    {
        var keys = Path.Combine(_dir, "keys");
        Directory.CreateDirectory(keys);
        var recipient = Path.Combine(keys, "recipient-public.pem");
        var signing = Path.Combine(keys, "signing-private.pem");
        if (!File.Exists(recipient))
        {
            File.WriteAllText(recipient, BackupKeys.ExportPublicPem(Recipient.Value));
            File.WriteAllText(signing, BackupKeys.ExportPrivatePem(Signing.Value, Password));
        }

        return new ExchangeSettings
        {
            InstanceId = instanceId,
            TargetFolder = targetFolder ?? Path.Combine(_dir, "medium"),
            RecipientKeyPath = recipientPath ?? recipient,
            RecipientKeySha256 = recipientSha256 ?? BackupKeys.FingerprintHex(Recipient.Value),
            SigningKeyPath = signingPath ?? signing,
            SigningKeySha256 = signingSha256 ?? BackupKeys.FingerprintHex(Signing.Value),
        };
    }

    /// <summary>Часы теста: «сейчас» задано, местное время — UTC.</summary>
    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }
}
