using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using ISC.AI.Backup;
using ISC.AI.Backup.Restore;
using Shouldly;
using Xunit;

namespace ISC.AI.UnitTests.Backup;

/// <summary>
/// Программа восстановления (ADR-0063) без базы: аргументы, создание ключей хранителем, проверка копии и отказы
/// восстановления до обращения к серверу — повреждённая копия, нет второго лица, одно лицо дважды, не тот ключ, каталог
/// файлов, которого нет в копии, и каталоги восстановления, наложенные друг на друга.
/// </summary>
public sealed class RestoreAppTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "iscai-restore-tests", Guid.NewGuid().ToString("N"));

    public RestoreAppTests() => Directory.CreateDirectory(_dir);

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

    [Fact(DisplayName = "Аргументы: без команды, неизвестная команда, ключ без значения, нет обязательного — код 2 и подсказка")]
    public async Task Usage_errors()
    {
        (await RunAsync([])).Code.ShouldBe(2);
        var unknown = await RunAsync(["drop-all"]);
        (unknown.Code, unknown.Output).ShouldSatisfyAllConditions(r => r.Code.ShouldBe(2), r => r.Output.ShouldContain("Неизвестная команда"));
        (await RunAsync(["verify", "--backup"])).Output.ShouldContain("не задано значение");
        (await RunAsync(["restore", "--backup", _dir])).Output.ShouldContain("--server");
        (await RunAsync(["help"])).Code.ShouldBe(0);
    }

    [Fact(DisplayName = "keygen: пара ключей, закрытый — под паролем, отпечаток напечатан; короткий или не повторённый пароль и повторное создание — отказ")]
    public async Task Keygen_creates_protected_pair()
    {
        var folder = Path.Combine(_dir, "keys");
        (await RunAsync(["keygen", "--out", folder], "короткий")).Code.ShouldBe(1);
        (await RunAsync(["keygen", "--out", folder], "пароль хранителя ключа", "другой пароль хранителя")).Code.ShouldBe(1);

        var created = await RunAsync(["keygen", "--out", folder], "пароль хранителя ключа", "пароль хранителя ключа");

        created.Code.ShouldBe(0);
        using var key = BackupKeys.LoadPrivateKey(Path.Combine(folder, RestoreApp.PrivateKeyFile), "пароль хранителя ключа");
        created.Output.ShouldContain(BackupKeys.FingerprintHex(key));
        using var publicKey = BackupKeys.LoadPublicKey(Path.Combine(folder, RestoreApp.PublicKeyFile));
        BackupKeys.FingerprintHex(publicKey).ShouldBe(BackupKeys.FingerprintHex(key));

        (await RunAsync(["keygen", "--out", folder], "пароль хранителя ключа", "пароль хранителя ключа")).Code.ShouldBe(1);
    }

    [Fact(DisplayName = "verify: целая копия — код 0; изменённый файл — код 1 и расхождение")]
    public async Task Verify_reports()
    {
        using var key = RSA.Create(BackupKeys.MinKeyBits);
        var backup = await BackupFormatTests.MakeBackupAsync(_dir, "isc-backup-2026-10-27_120000", key, [1, 2, 3], [4, 5]);

        var ok = await RunAsync(["verify", "--backup", backup, "--fingerprint", BackupKeys.FingerprintHex(key)]);
        ok.Code.ShouldBe(0);
        ok.Output.ShouldContain("Копия цела");

        ok.Output.ShouldContain("файлы «docflow»");

        File.AppendAllText(Path.Combine(backup, "files-core.tar.enc"), "x");
        var broken = await RunAsync(["verify", "--backup", backup]);
        broken.Code.ShouldBe(1);
        broken.Output.ShouldContain("files-core.tar.enc");
    }

    [Fact(DisplayName = "restore: повреждённая копия, нет ФИО, одно лицо в двух ролях, не тот ключ — отказ до обращения к серверу")]
    public async Task Restore_refuses_before_server()
    {
        using var key = RSA.Create(BackupKeys.MinKeyBits);
        using var stranger = RSA.Create(BackupKeys.MinKeyBits);
        var backup = await BackupFormatTests.MakeBackupAsync(_dir, "isc-backup-2026-10-27_120000", key, [1, 2, 3], [4, 5]);
        var strangerKey = Path.Combine(_dir, "stranger.pem");
        File.WriteAllText(strangerKey, BackupKeys.ExportPrivatePem(stranger, "пароль хранителя ключа"));
        string[] args = ["restore", "--backup", backup, "--server", "Host=127.0.0.1;Port=1;Username=postgres"];

        var noSecond = await RunAsync(args, "Петров П.П.", "");
        (noSecond.Code, noSecond.Output.Contains("ФИО обоих")).ShouldBe((1, true));

        var samePerson = await RunAsync(args, "Петров П.П.", "петров п.п.");
        (samePerson.Code, samePerson.Output.Contains("разные люди")).ShouldBe((1, true));

        var wrongKey = await RunAsync([.. args, "--key", strangerKey], "Петров П.П.", "Сидоров С.С.", "пароль хранителя ключа");
        (wrongKey.Code, wrongKey.Output.Contains("Ключ не подходит")).ShouldBe((1, true));

        File.AppendAllText(Path.Combine(backup, "files-core.tar.enc"), "x");
        var broken = await RunAsync(args);
        (broken.Code, broken.Output.Contains("Копия НЕ цела")).ShouldBe((1, true));
    }

    [Fact(DisplayName = "restore: --files-<имя> каталога, которого нет в копии, и каталоги файлов друг в друге — код 2 до вопросов")]
    public async Task Restore_rejects_bad_file_targets()
    {
        using var key = RSA.Create(BackupKeys.MinKeyBits);
        var backup = await BackupFormatTests.MakeBackupAsync(_dir, "isc-backup-2026-10-27_120000", key, [1, 2, 3], [4, 5]);
        string[] args = ["restore", "--backup", backup, "--server", "Host=127.0.0.1;Port=1;Username=postgres"];

        var unknown = await RunAsync([.. args, "--files-media", Path.Combine(_dir, "media")]);
        (unknown.Code, unknown.Output.Contains("нет каталога файлов «media»")).ShouldBe((2, true));

        var nested = await RunAsync([.. args, "--files-docflow", Path.Combine(_dir, "storage", "docflow")]);
        (nested.Code, nested.Output.Contains("совпадают или вложены")).ShouldBe((2, true));
        nested.Output.ShouldNotContain("ФИО Администратора");
    }

    private static async Task<(int Code, string Output)> RunAsync(string[] args, params string[] answers)
    {
        var console = new ScriptedConsole(answers);
        var code = await RestoreApp.RunAsync(args, console);
        return (code, console.Output);
    }

    /// <summary>Консоль со сценарием ответов: и видимые, и скрытые вопросы берут ответы по порядку.</summary>
    private sealed class ScriptedConsole(IEnumerable<string> answers) : IRestoreConsole
    {
        private readonly Queue<string> _answers = new(answers);
        private readonly List<string> _lines = [];

        public string Output => string.Join("\n", _lines);

        public void WriteLine(string text = "") => _lines.Add(text);

        public string? Ask(string prompt)
        {
            _lines.Add(prompt);
            return _answers.Count > 0 ? _answers.Dequeue() : null;
        }

        public string? AskSecret(string prompt) => Ask(prompt);
    }
}
