using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using ISC.AI.Abstractions.Exchange;
using ISC.AI.Backup;
using Shouldly;
using Xunit;

namespace ISC.AI.UnitTests.Backup;

/// <summary>
/// Запечатанный пакет ядра (ADR-0068, ТБ-083, ТО-инф-14) без базы: сигнатуры форматов «копия» и «обмен» не путаются, пакет
/// собирается во временном каталоге и появляется целиком, проверка без ключа получателя ловит подмену подписи, манифеста,
/// содержимого и состава, недописанный пакет убирается.
/// </summary>
public sealed class SealedPackageTests : IDisposable
{
    // Ключи 3072 бит — наименьшие допустимые: тесты быстрее.
    private static readonly Lazy<RSA> Recipient = new(() => RSA.Create(BackupKeys.MinKeyBits));
    private static readonly Lazy<RSA> Signing = new(() => RSA.Create(BackupKeys.MinKeyBits));
    private static readonly Lazy<RSA> Stranger = new(() => RSA.Create(BackupKeys.MinKeyBits));

    private static readonly byte[] Data = Encoding.UTF8.GetBytes("{\"cases\":[\"ОУ-2/17-2026\"]}");
    private static readonly byte[] Media = RandomNumberGenerator.GetBytes(SealedFile.ChunkSize + 777);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "iscai-sealed-tests", Guid.NewGuid().ToString("N"));

    public SealedPackageTests() => Directory.CreateDirectory(_dir);

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

    [Fact(DisplayName = "Форматы: файл пакета обмена не читается как копия и наоборот — отказ по сигнатуре; свой формат — туда и обратно")]
    public async Task Exchange_and_backup_files_are_not_interchangeable()
    {
        var exchange = await EncryptAsync(Data, SealedFormat.Exchange, Recipient.Value);
        using var backup = new MemoryStream();
        await BackupCipher.EncryptAsync(new MemoryStream(Data), backup, Recipient.Value);

        (await DecryptAsync(exchange, SealedFormat.Exchange, Recipient.Value)).ShouldBe(Data);
        Encoding.ASCII.GetString(exchange, 0, 8).ShouldBe("ISCXCH01");
        Encoding.ASCII.GetString(backup.ToArray(), 0, 8).ShouldBe("ISCBAK01");

        (await Should.ThrowAsync<InvalidDataException>(() => DecryptAsync(exchange, SealedFormat.Backup, Recipient.Value)))
            .Message.ShouldContain("ISCBAK01");
        (await Should.ThrowAsync<InvalidDataException>(() => DecryptAsync(backup.ToArray(), SealedFormat.Exchange, Recipient.Value)))
            .Message.ShouldContain("пакета обмена");
        (await SealedFile.ReadFingerprintAsync(new MemoryStream(exchange), SealedFormat.Exchange))
            .ShouldBe(BackupKeys.FingerprintHex(Recipient.Value));
    }

    [Fact(DisplayName = "Подпись манифеста: своя сходится; изменённые данные, чужой ключ и подпись не той длины — «не сходится», без исключения")]
    public void Signature_detects_change()
    {
        var signature = SealedSignature.Sign(Data, Signing.Value);

        SealedSignature.Verify(Data, signature, Signing.Value).ShouldBeTrue();
        SealedSignature.Verify([.. Data, 0x20], signature, Signing.Value).ShouldBeFalse();
        SealedSignature.Verify(Data, signature, Stranger.Value).ShouldBeFalse();
        SealedSignature.Verify(Data, signature.AsSpan(0, 17), Signing.Value).ShouldBeFalse();
    }

    [Fact(DisplayName = "Пакет: собирается в «.partial» и появляется целиком; проверка без ключа получателя — без расхождений; файлы расшифровываются")]
    public async Task Package_round_trip_verifies()
    {
        var folder = await MakePackageAsync("isc-exchange-office-2-2026-10-2026-11-01_090000");

        Directory.Exists(folder + SealedPackageWriter.PartialSuffix).ShouldBeFalse();
        var check = await SealedPackageVerifier.VerifyAsync(
            folder, Signing.Value, SealedFormat.Exchange, BackupKeys.FingerprintHex(Recipient.Value).ToLowerInvariant());

        check.Problems.ShouldBeEmpty();
        check.Ok.ShouldBeTrue();
        var manifest = check.Manifest.ShouldNotBeNull();
        manifest.Files.Select(f => f.Name).ShouldBe(["data.json.enc", "media/000001.enc"]);
        manifest.Files.ShouldAllBe(f => f.Bytes == new FileInfo(Path.Combine(folder, f.Name)).Length);
        manifest.Counts["cases"].ShouldBe(1);
        (await DecryptFileAsync(Path.Combine(folder, "data.json.enc"))).ShouldBe(Data);
        (await DecryptFileAsync(Path.Combine(folder, "media", "000001.enc"))).ShouldBe(Media);
    }

    [Fact(DisplayName = "Проверка пакета: чужой ключ источника, правка манифеста, испорченная подпись и нет манифеста — отказ до чтения состава")]
    public async Task Signature_problems_stop_the_check()
    {
        var folder = await MakePackageAsync("isc-exchange-office-2-2026-10-2026-11-01_090000");

        var foreign = await SealedPackageVerifier.VerifyAsync(folder, Stranger.Value, SealedFormat.Exchange);
        foreign.Manifest.ShouldBeNull();
        foreign.Problems.ShouldHaveSingleItem().ShouldContain("Подпись манифеста не сходится");

        var manifestPath = Path.Combine(folder, SealedPackageManifest.FileName);
        var original = await File.ReadAllTextAsync(manifestPath);
        await File.WriteAllTextAsync(manifestPath, original.Replace("\"cases\": 1", "\"cases\": 2", StringComparison.Ordinal));
        (await SealedPackageVerifier.VerifyAsync(folder, Signing.Value, SealedFormat.Exchange)).Manifest.ShouldBeNull();
        await File.WriteAllTextAsync(manifestPath, original);

        var signaturePath = Path.Combine(folder, SealedPackageManifest.SignatureFileName);
        await File.WriteAllTextAsync(signaturePath, "это не подпись");
        (await SealedPackageVerifier.VerifyAsync(folder, Signing.Value, SealedFormat.Exchange)).Problems
            .ShouldHaveSingleItem().ShouldContain("не Base64");

        File.Delete(signaturePath);
        (await SealedPackageVerifier.VerifyAsync(folder, Signing.Value, SealedFormat.Exchange)).Problems
            .ShouldHaveSingleItem().ShouldContain("нет манифеста");
    }

    [Fact(DisplayName = "Проверка пакета: подменённый байт, другой объём, пропавший и подброшенный файл, чужой получатель — расхождения поимённо")]
    public async Task Content_problems_are_listed()
    {
        var folder = await MakePackageAsync("isc-exchange-office-2-2026-10-2026-11-01_090000");
        var media = Path.Combine(folder, "media", "000001.enc");
        var bytes = await File.ReadAllBytesAsync(media);
        bytes[^30] ^= 0x01;
        await File.WriteAllBytesAsync(media, bytes);
        await File.AppendAllTextAsync(Path.Combine(folder, "data.json.enc"), "x");
        await File.WriteAllTextAsync(Path.Combine(folder, "media", "000002.enc"), "подброшено");

        var check = await SealedPackageVerifier.VerifyAsync(
            folder, Signing.Value, SealedFormat.Exchange, BackupKeys.FingerprintHex(Stranger.Value));

        check.Ok.ShouldBeFalse();
        check.Manifest.ShouldNotBeNull();
        check.Problems.ShouldContain(p => p.Contains("media/000001.enc") && p.Contains("SHA-256"));
        check.Problems.ShouldContain(p => p.StartsWith("data.json.enc: объём", StringComparison.Ordinal));
        check.Problems.ShouldContain("Лишний файл вне манифеста: media/000002.enc.");
        check.Problems.ShouldContain(p => p.StartsWith("Пакет зашифрован для другого ключа", StringComparison.Ordinal));

        File.Delete(media);
        (await SealedPackageVerifier.VerifyAsync(folder, Signing.Value, SealedFormat.Exchange)).Problems
            .ShouldContain("Нет файла media/000001.enc.");
    }

    [Fact(DisplayName = "Проверка пакета: файл зашифрован не тем ключом, что в манифесте, или имя повторено — расхождение")]
    public async Task Manifest_must_match_the_files()
    {
        var folder = await MakePackageAsync(
            "isc-exchange-office-2-2026-10-2026-11-01_090000",
            files => Manifest(files, recipientFingerprint: BackupKeys.FingerprintHex(Stranger.Value)));

        (await SealedPackageVerifier.VerifyAsync(folder, Signing.Value, SealedFormat.Exchange)).Problems
            .ShouldContain(p => p.StartsWith("data.json.enc: зашифрован ключом", StringComparison.Ordinal));

        var twice = await MakePackageAsync(
            "isc-exchange-office-2-2026-10-2026-11-02_090000", files => Manifest([.. files, files[0]]));
        (await SealedPackageVerifier.VerifyAsync(twice, Signing.Value, SealedFormat.Exchange)).Problems
            .ShouldContain("Один и тот же файл указан в манифесте дважды.");
    }

    [Fact(DisplayName = "Сборка: недописанный пакет убирается; повтор имени каталога, обход каталога, служебные и повторные имена файлов — отказ")]
    public async Task Writer_guards_names_and_cleans_up()
    {
        const string name = "isc-exchange-office-2-2026-10-2026-11-01_090000";
        await using (var writer = SealedPackageWriter.Create(_dir, name, Recipient.Value, SealedFormat.Exchange))
        {
            await writer.AddAsync("data.json.enc", new MemoryStream(Data));
            Directory.Exists(writer.PartialPath).ShouldBeTrue();

            foreach (var bad in new[] { "../outside.enc", "media//x.enc", "media/./x.enc", "C:/x.enc", "файл.enc", "a\\b.enc",
                                        SealedPackageManifest.FileName, SealedPackageManifest.SignatureFileName, "data.json.enc" })
            {
                await Should.ThrowAsync<ArgumentException>(() => writer.AddAsync(bad, new MemoryStream(Data)));
            }

            Should.Throw<IOException>(() => SealedPackageWriter.Create(_dir, name, Recipient.Value, SealedFormat.Exchange));
        }

        Directory.Exists(Path.Combine(_dir, name + SealedPackageWriter.PartialSuffix)).ShouldBeFalse();
        Directory.Exists(Path.Combine(_dir, name)).ShouldBeFalse();
        File.Exists(Path.Combine(_dir, "outside.enc")).ShouldBeFalse();
        Should.Throw<ArgumentException>(() => SealedPackageWriter.Create(_dir, "../escape", Recipient.Value, SealedFormat.Exchange));
    }

    [Fact(DisplayName = "Порт ядра: отпечатки ключей и список файлов в манифест пишет ядро — по своим ключам и файлам; SHA-256 манифеста — тех байтов, что на носителе")]
    public async Task Core_port_fills_fingerprints_and_files()
    {
        var packages = new ExchangePackages();
        ExchangePackageSealed done;
        await using (var builder = packages.Create(_dir, "isc-exchange-office-2-2026-10-2026-11-01_090000", Recipient.Value))
        {
            await builder.AddAsync("data.json.enc", new MemoryStream(Data));
            await builder.AddAsync("media/000001.enc", new MemoryStream(Media));
            done = await builder.CompleteAsync(Content(), Signing.Value);
        }

        var folder = Path.Combine(_dir, done.FolderName);
        done.FileCount.ShouldBe(2);
        done.Bytes.ShouldBe(Directory.EnumerateFiles(folder, "*.enc", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length));
        done.RecipientKeyFingerprint.ShouldBe(BackupKeys.FingerprintHex(Recipient.Value));
        done.SigningKeyFingerprint.ShouldBe(BackupKeys.FingerprintHex(Signing.Value));
        done.ManifestSha256.ShouldBe(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(folder, SealedPackageManifest.FileName)))));

        var check = await SealedPackageVerifier.VerifyAsync(folder, Signing.Value, SealedFormat.Exchange, done.RecipientKeyFingerprint);
        check.Ok.ShouldBeTrue(string.Join("; ", check.Problems));
        check.Manifest!.Kind.ShouldBe("isc-exchange");
        check.Manifest.Files.Count.ShouldBe(2);
    }

    [Fact(DisplayName = "Порт ядра: ключи — только с закреплённым отпечатком и верным паролем; недописанные пакеты с префиксом убираются, чужие каталоги — нет")]
    public async Task Core_port_pins_keys_and_removes_unfinished()
    {
        var packages = new ExchangePackages();
        var recipientPem = Path.Combine(_dir, "recipient.pem");
        var signingPem = Path.Combine(_dir, "signing.pem");
        await File.WriteAllTextAsync(recipientPem, BackupKeys.ExportPublicPem(Recipient.Value));
        await File.WriteAllTextAsync(signingPem, BackupKeys.ExportPrivatePem(Signing.Value, "пароль"));
        var recipientPin = BackupKeys.FingerprintHex(Recipient.Value);
        var signingPin = BackupKeys.FingerprintHex(Signing.Value);

        using (var key = packages.LoadRecipientKey(recipientPem, " " + recipientPin.ToLowerInvariant() + " "))
        {
            packages.Fingerprint(key).ShouldBe(recipientPin);
        }

        using (var key = packages.LoadSigningKey(signingPem, signingPin, "пароль"))
        {
            packages.Fingerprint(key).ShouldBe(signingPin);
        }

        Should.Throw<InvalidOperationException>(() => packages.LoadRecipientKey(recipientPem, signingPin)).Message.ShouldContain("не совпал");
        Should.Throw<InvalidOperationException>(() => packages.LoadRecipientKey(Path.Combine(_dir, "нет.pem"), recipientPin));
        Should.Throw<InvalidOperationException>(() => packages.LoadSigningKey(signingPem, signingPin, "не тот")).Message.ShouldContain("Пароль");
        Should.Throw<InvalidOperationException>(() => packages.LoadSigningKey(signingPem, recipientPin, "пароль")).Message.ShouldContain("не совпал");
        Should.Throw<InvalidOperationException>(() => packages.LoadSigningKey(recipientPem, signingPin, "пароль"));

        Directory.CreateDirectory(Path.Combine(_dir, "isc-exchange-office-2-2026-10-x" + SealedPackageWriter.PartialSuffix, "media"));
        Directory.CreateDirectory(Path.Combine(_dir, "isc-exchange-office-2-2026-09-y"));
        Directory.CreateDirectory(Path.Combine(_dir, "other" + SealedPackageWriter.PartialSuffix));

        packages.RemoveUnfinished(_dir, "isc-exchange-").ShouldBe(1);
        Directory.Exists(Path.Combine(_dir, "isc-exchange-office-2-2026-10-x" + SealedPackageWriter.PartialSuffix)).ShouldBeFalse();
        Directory.Exists(Path.Combine(_dir, "isc-exchange-office-2-2026-09-y")).ShouldBeTrue();
        Directory.Exists(Path.Combine(_dir, "other" + SealedPackageWriter.PartialSuffix)).ShouldBeTrue();
        packages.RemoveUnfinished(Path.Combine(_dir, "нет-носителя"), "isc-exchange-").ShouldBe(0);
    }

    [Fact(DisplayName = "Манифест: туда и обратно без потерь, кириллица — как есть; не JSON — понятный отказ")]
    public void Manifest_round_trip()
    {
        var manifest = Manifest([new SealedPackageFile("data.json.enc", 10, "AB")]);

        var bytes = manifest.ToBytes();
        Encoding.UTF8.GetString(bytes).ShouldContain("секретно");
        var back = SealedPackageManifest.FromBytes(bytes);

        back.ToBytes().ShouldBe(bytes);
        back.Selection["cases"].ShouldBe("ОУ-2/17-2026");
        Should.Throw<InvalidDataException>(() => SealedPackageManifest.FromBytes(Encoding.UTF8.GetBytes("не JSON")));
    }

    // ---------- окружение ----------

    private async Task<string> MakePackageAsync(
        string name, Func<IReadOnlyList<SealedPackageFile>, SealedPackageManifest>? manifestFor = null)
    {
        await using var writer = SealedPackageWriter.Create(_dir, name, Recipient.Value, SealedFormat.Exchange);
        await writer.AddAsync("data.json.enc", new MemoryStream(Data));
        await writer.AddAsync("media/000001.enc", new MemoryStream(Media));
        writer.BytesWritten.ShouldBe(writer.Files.Sum(f => f.Bytes));
        await writer.CompleteAsync(manifestFor ?? (files => Manifest(files)), Signing.Value);
        return writer.FinalPath;
    }

    private static SealedPackageManifest Manifest(IReadOnlyList<SealedPackageFile> files, string? recipientFingerprint = null) => new(
        SealedPackageManifest.CurrentFormat,
        "isc-exchange",
        "1.0-test",
        "office-2",
        "2026-10",
        new DateTimeOffset(2026, 11, 1, 9, 0, 0, TimeSpan.FromHours(6)),
        7,
        3,
        "секретно",
        recipientFingerprint ?? BackupKeys.FingerprintHex(Recipient.Value),
        BackupKeys.FingerprintHex(Signing.Value),
        new Dictionary<string, string>(StringComparer.Ordinal) { ["period"] = "2026-10", ["cases"] = "ОУ-2/17-2026" },
        new Dictionary<string, int>(StringComparer.Ordinal) { ["cases"] = 1, ["files"] = 1 },
        files);

    private static ExchangeManifestContent Content() => new(
        "isc-exchange",
        "1.0-test",
        "office-2",
        "2026-10",
        new DateTimeOffset(2026, 11, 1, 9, 0, 0, TimeSpan.FromHours(6)),
        7,
        3,
        "секретно",
        new Dictionary<string, string>(StringComparer.Ordinal) { ["period"] = "2026-10" },
        new Dictionary<string, int>(StringComparer.Ordinal) { ["cases"] = 1 });

    private static async Task<byte[]> EncryptAsync(byte[] plain, SealedFormat format, RSA key)
    {
        using var output = new MemoryStream();
        await SealedFile.EncryptAsync(new MemoryStream(plain), output, key, format);
        return output.ToArray();
    }

    private static async Task<byte[]> DecryptAsync(byte[] encrypted, SealedFormat format, RSA key)
    {
        using var output = new MemoryStream();
        await SealedFile.DecryptAsync(new MemoryStream(encrypted), output, key, format);
        return output.ToArray();
    }

    private static async Task<byte[]> DecryptFileAsync(string path) =>
        await DecryptAsync(await File.ReadAllBytesAsync(path), SealedFormat.Exchange, Recipient.Value);
}
