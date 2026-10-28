using System;
using System.Collections.Generic;
using System.Formats.Tar;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using ISC.AI.Backup;
using Shouldly;
using Xunit;

namespace ISC.AI.UnitTests.Backup;

/// <summary>
/// Формат резервной копии (ТБ-082, ADR-0063) без базы и <c>pg_dump</c>: шифрование открытым ключом (туда и обратно,
/// подмена, обрыв, перестановка, чужой ключ), ключи хранителя, архив хранилища (пути, отметки, обход каталога) и проверка
/// манифеста без закрытого ключа.
/// </summary>
public sealed class BackupFormatTests : IDisposable
{
    // Ключ 3072 бит — наименьший допустимый: тесты быстрее, чем с 4096 у keygen.
    private static readonly Lazy<RSA> Key = new(() => RSA.Create(BackupKeys.MinKeyBits));
    private static readonly Lazy<RSA> OtherKey = new(() => RSA.Create(BackupKeys.MinKeyBits));

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "iscai-backup-tests", Guid.NewGuid().ToString("N"));

    public BackupFormatTests() => Directory.CreateDirectory(_dir);

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

    [Theory(DisplayName = "Шифрование: туда и обратно без потерь — пусто, мало, ровно порция, несколько порций")]
    [InlineData(0)]
    [InlineData(17)]
    [InlineData(BackupCipher.ChunkSize)]
    [InlineData(BackupCipher.ChunkSize * 2 + 12345)]
    public async Task Cipher_round_trip(int size)
    {
        var plain = RandomNumberGenerator.GetBytes(size);

        var encrypted = await EncryptAsync(plain);
        var decrypted = await DecryptAsync(encrypted, Key.Value);

        decrypted.ShouldBe(plain);
        if (size > 64)
        {
            encrypted.AsSpan().IndexOf(plain.AsSpan(0, 64)).ShouldBe(-1, "открытого текста в файле нет");
        }
    }

    [Fact(DisplayName = "Шифрование: изменённый байт, отрезанный хвост, дописанные данные и переставленные порции — отказ")]
    public async Task Cipher_detects_tampering()
    {
        var plain = RandomNumberGenerator.GetBytes(BackupCipher.ChunkSize * 2 + 100);
        var encrypted = await EncryptAsync(plain);

        var flipped = (byte[])encrypted.Clone();
        flipped[^100] ^= 0x01;
        await Should.ThrowAsync<CryptographicException>(() => DecryptAsync(flipped, Key.Value));

        await Should.ThrowAsync<InvalidDataException>(() => DecryptAsync(encrypted[..^200], Key.Value));
        await Should.ThrowAsync<InvalidDataException>(() => DecryptAsync([.. encrypted, 0x00], Key.Value));

        // Перестановка первых двух порций (одинаковой длины): тег проверяется с номером порции — не пройдёт.
        var header = encrypted.Length - (3 * (5 + 16) + plain.Length);
        var chunk = 5 + BackupCipher.ChunkSize + 16;
        var swapped = (byte[])encrypted.Clone();
        encrypted.AsSpan(header, chunk).CopyTo(swapped.AsSpan(header + chunk));
        encrypted.AsSpan(header + chunk, chunk).CopyTo(swapped.AsSpan(header));
        await Should.ThrowAsync<CryptographicException>(() => DecryptAsync(swapped, Key.Value));
    }

    [Fact(DisplayName = "Шифрование: чужой ключ — понятный отказ с отпечатками; не файл копии — отказ по сигнатуре")]
    public async Task Cipher_rejects_wrong_key_and_foreign_file()
    {
        var encrypted = await EncryptAsync(Encoding.UTF8.GetBytes("материалы дела"));

        var error = await Should.ThrowAsync<CryptographicException>(() => DecryptAsync(encrypted, OtherKey.Value));
        error.Message.ShouldContain("другим ключом");

        await Should.ThrowAsync<InvalidDataException>(() => DecryptAsync(Encoding.UTF8.GetBytes("не копия"), Key.Value));
        (await BackupCipher.ReadFingerprintAsync(new MemoryStream(encrypted))).ShouldBe(BackupKeys.FingerprintHex(Key.Value));
    }

    [Fact(DisplayName = "Ключи: закрытый под паролем читается только с ним; отпечаток у пары один; короткий открытый ключ — отказ")]
    public void Keys_round_trip()
    {
        var privatePath = Path.Combine(_dir, "private.pem");
        var publicPath = Path.Combine(_dir, "public.pem");
        File.WriteAllText(privatePath, BackupKeys.ExportPrivatePem(Key.Value, "длинный пароль хранителя"));
        File.WriteAllText(publicPath, BackupKeys.ExportPublicPem(Key.Value));

        using var loadedPrivate = BackupKeys.LoadPrivateKey(privatePath, "длинный пароль хранителя");
        using var loadedPublic = BackupKeys.LoadPublicKey(publicPath);
        BackupKeys.FingerprintHex(loadedPrivate).ShouldBe(BackupKeys.FingerprintHex(Key.Value));
        BackupKeys.FingerprintHex(loadedPublic).ShouldBe(BackupKeys.FingerprintHex(Key.Value));
        File.ReadAllText(privatePath).ShouldContain("ENCRYPTED PRIVATE KEY");

        Should.Throw<CryptographicException>(() => BackupKeys.LoadPrivateKey(privatePath, "не тот пароль"));

        using var weak = RSA.Create(2048);
        var weakPath = Path.Combine(_dir, "weak.pem");
        File.WriteAllText(weakPath, BackupKeys.ExportPublicPem(weak));
        Should.Throw<InvalidOperationException>(() => BackupKeys.LoadPublicKey(weakPath)).Message.ShouldContain("3072");
    }

    [Fact(DisplayName = "Хранилище: архив туда и обратно — вложенные каталоги и кириллица; отметки о восстановлении в копию не идут")]
    public async Task Storage_round_trip()
    {
        var source = Path.Combine(_dir, "storage");
        Write(source, "media-originals/7/a.bin", [1, 2, 3]);
        Write(source, "docflow/Документ.txt", Encoding.UTF8.GetBytes("текст"));
        Write(source, "empty.dat", []);
        Write(source, StorageArchive.RestoreJournalFolder + "/restore-1.json", [9]);

        using var archive = new MemoryStream();
        var packed = await StorageArchive.WriteAsync(source, archive);
        packed.ShouldBe(new StorageArchiveResult(3, 3 + 10)); // «текст» в UTF-8 — 10 байт

        archive.Position = 0;
        var target = Path.Combine(_dir, "restored");
        var unpacked = await StorageArchive.ExtractAsync(archive, target);

        unpacked.ShouldBe(packed);
        File.ReadAllBytes(Path.Combine(target, "media-originals", "7", "a.bin")).ShouldBe(new byte[] { 1, 2, 3 });
        File.ReadAllText(Path.Combine(target, "docflow", "Документ.txt")).ShouldBe("текст");
        Directory.Exists(Path.Combine(target, StorageArchive.RestoreJournalFolder)).ShouldBeFalse();
    }

    [Fact(DisplayName = "Хранилище: путь за пределы каталога в архиве (подложенная копия) — отказ, файл мимо хранилища не пишется")]
    public async Task Storage_rejects_path_traversal()
    {
        using var archive = new MemoryStream();
        await using (var writer = new TarWriter(archive, TarEntryFormat.Pax, leaveOpen: true))
        {
            await writer.WriteEntryAsync(new PaxTarEntry(TarEntryType.RegularFile, "../evil.txt") { DataStream = new MemoryStream([1]) });
        }

        archive.Position = 0;
        await Should.ThrowAsync<InvalidDataException>(() => StorageArchive.ExtractAsync(archive, Path.Combine(_dir, "restored")));
        File.Exists(Path.Combine(_dir, "evil.txt")).ShouldBeFalse();
    }

    [Fact(DisplayName = "Отметка «система запущена»: занята хостом — видна программе восстановления и второму хосту; отпущена или её нет — свободна")]
    public void Host_presence_lock()
    {
        var storage = Path.Combine(_dir, "storage");
        HostPresence.IsHeld(storage).ShouldBeFalse();

        using (HostPresence.TryHold(storage).ShouldNotBeNull())
        {
            HostPresence.IsHeld(storage).ShouldBeTrue();
            HostPresence.TryHold(storage).ShouldBeNull();
        }

        HostPresence.IsHeld(storage).ShouldBeFalse();
    }

    [Fact(DisplayName = "Подмена каталога файлов: не вышло второе переименование — прежний на месте; откат возвращает прежний, восстановленный — рядом")]
    public void Storage_swap_compensates_and_undoes()
    {
        var root = Path.Combine(_dir, "files");
        var previous = root + ".before";
        var restored = root + ".restore";
        Write(root, "a.txt", [1]);

        Should.Throw<DirectoryNotFoundException>(() => BackupRestorer.SwapStorage(root, Path.Combine(_dir, "нет"), previous));
        File.Exists(Path.Combine(root, "a.txt")).ShouldBeTrue();
        Directory.Exists(previous).ShouldBeFalse();

        Write(restored, "b.txt", [2]);
        BackupRestorer.SwapStorage(root, restored, previous).ShouldBeTrue();
        File.Exists(Path.Combine(root, "b.txt")).ShouldBeTrue();

        BackupRestorer.UndoSwapStorage(root, restored, previous);
        File.Exists(Path.Combine(root, "a.txt")).ShouldBeTrue();
        File.Exists(Path.Combine(restored, "b.txt")).ShouldBeTrue();
        Directory.Exists(previous).ShouldBeFalse();
    }

    [Theory(DisplayName = "Клиент PostgreSQL: основная версия — из вывода --version и из версии сервера; не разобрать — 0")]
    [InlineData("pg_dump (PostgreSQL) 16.4", 16)]
    [InlineData("pg_restore (PostgreSQL) 18.3", 18)]
    [InlineData("16.14 (Debian 16.14-1.pgdg12+1)", 16)]
    [InlineData("17beta2", 17)]
    [InlineData("", 0)]
    [InlineData(null, 0)]
    public void Pg_major_version_is_parsed(string? version, int major) => PgTools.MajorVersion(version).ShouldBe(major);

    [Fact(DisplayName = "Проверка манифеста: целая копия — без расхождений; последняя готовая — по имени, недописанная не в счёт")]
    public async Task Verifier_accepts_intact_backup()
    {
        var folder = await MakeBackupAsync("isc-backup-2026-10-27_120000");
        Directory.CreateDirectory(Path.Combine(_dir, "isc-backup-2026-10-28_090000" + BackupWriter.PartialSuffix));

        var check = await BackupVerifier.VerifyAsync(folder, BackupKeys.FingerprintHex(Key.Value));

        check.Problems.ShouldBeEmpty();
        check.Ok.ShouldBeTrue();
        BackupVerifier.FindLatest(_dir).ShouldBe(folder);
    }

    [Fact(DisplayName = "Проверка манифеста: нет файла, другой размер, подменённое содержимое, не действующий ключ, нет манифеста — расхождения")]
    public async Task Verifier_reports_problems()
    {
        var folder = await MakeBackupAsync("isc-backup-2026-10-27_120000");
        var db = Path.Combine(folder, "db-ISC_AI.dump.enc");
        var bytes = File.ReadAllBytes(db);
        bytes[^1] ^= 0xFF;
        File.WriteAllBytes(db, bytes);
        File.AppendAllText(Path.Combine(folder, "files-core.tar.enc"), "x");

        var check = await BackupVerifier.VerifyAsync(folder, BackupKeys.FingerprintHex(OtherKey.Value));

        check.Ok.ShouldBeFalse();
        check.Problems.ShouldContain(p => p.Contains("не действующим ключом"));
        check.Problems.ShouldContain(p => p.Contains("db-ISC_AI.dump.enc") && p.Contains("SHA-256"));
        check.Problems.ShouldContain(p => p.Contains("Размер files-core.tar.enc"));

        File.Delete(Path.Combine(folder, "files-docflow.tar.enc"));
        (await BackupVerifier.VerifyAsync(folder, null)).Problems.ShouldContain("Нет файла files-docflow.tar.enc.");

        File.Delete(db);
        (await BackupVerifier.VerifyAsync(folder, null)).Problems.ShouldContain("Нет файла db-ISC_AI.dump.enc.");

        File.Delete(Path.Combine(folder, BackupManifest.FileName));
        (await BackupVerifier.VerifyAsync(folder, null)).Problems.ShouldBe(["Нет манифеста копии."]);
    }

    /// <summary>
    /// Каталог копии с манифестом — как делает <see cref="BackupWriter"/>, без pg_dump: база и два каталога файлов —
    /// хранилище ядра (<c>&lt;root&gt;/storage</c>) и документооборота (<c>&lt;root&gt;/docflow-files</c>).
    /// </summary>
    internal static async Task<string> MakeBackupAsync(string root, string name, RSA key, byte[] database, byte[] storage)
    {
        var folder = Path.Combine(root, name);
        Directory.CreateDirectory(folder);
        var files = new List<BackupFileEntry>();
        byte[] docflow = [7, 7, 7];
        foreach (var (file, content) in new[] { ("db-ISC_AI.dump.enc", database), ("files-core.tar.enc", storage), ("files-docflow.tar.enc", docflow) })
        {
            var path = Path.Combine(folder, file);
            await using (var output = File.Create(path))
            {
                await BackupCipher.EncryptAsync(new MemoryStream(content), output, key);
            }

            files.Add(new BackupFileEntry(file, new FileInfo(path).Length, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))));
        }

        var manifest = new BackupManifest(
            BackupManifest.CurrentFormat, "1.0-test", new DateTimeOffset(2026, 10, 27, 12, 0, 0, TimeSpan.FromHours(6)), 7,
            BackupKeys.FingerprintHex(key),
            [new BackupDatabaseEntry("ISC_AI", "localhost", 5432, "17.2", "pg_dump (PostgreSQL) 18.0", "db-ISC_AI.dump.enc")],
            [
                new BackupStorageEntry("core", Path.Combine(root, "storage"), 1, storage.Length, BackupWriter.StorageFileFor("core")),
                new BackupStorageEntry("docflow", Path.Combine(root, "docflow-files"), 1, docflow.Length, BackupWriter.StorageFileFor("docflow")),
            ],
            files);
        await manifest.SaveAsync(folder);
        return folder;
    }

    private Task<string> MakeBackupAsync(string name) =>
        MakeBackupAsync(_dir, name, Key.Value, RandomNumberGenerator.GetBytes(5000), RandomNumberGenerator.GetBytes(3000));

    private static async Task<byte[]> EncryptAsync(byte[] plain)
    {
        using var output = new MemoryStream();
        (await BackupCipher.EncryptAsync(new MemoryStream(plain), output, Key.Value)).ShouldBe(plain.Length);
        return output.ToArray();
    }

    private static async Task<byte[]> DecryptAsync(byte[] encrypted, RSA key)
    {
        using var output = new MemoryStream();
        await BackupCipher.DecryptAsync(new MemoryStream(encrypted), output, key);
        return output.ToArray();
    }

    private static void Write(string root, string relative, byte[] content)
    {
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
    }
}
