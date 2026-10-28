using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Backup;
using ISC.AI.Backup;
using ISC.AI.Backup.Restore;
using ISC.AI.IntegrationTests.Persistence;
using ISC.AI.Persistence;
using ISC.AI.Persistence.Audit;
using ISC.AI.Persistence.Backup;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Shouldly;
using Testcontainers.PostgreSql;
using Xunit;

namespace ISC.AI.IntegrationTests.Backup;

/// <summary>
/// Резервная копия от и до на настоящем PostgreSQL (ТФ-АДМ-04, ТБ-082, ADR-0063): служба копий (журнал, одна копия за раз,
/// итог в аудите) с настоящим <c>pg_dump</c> и шифрованием, оба каталога файлов — хранилище ядра и документооборота,
/// проверка манифеста, восстановление программой ISC.AI.Backup.Restore (двумя лицами, с ключом под паролем) без
/// уничтожения прежних базы и каталогов, и запись восстановления в журнал аудита при «старте хоста».
/// </summary>
/// <remarks>
/// Требуются Docker и клиент PostgreSQL на машине (<c>Program Files\PostgreSQL\&lt;версия&gt;</c>). Клиент должен быть той же
/// основной версии, что сервер (иначе программа справедливо откажет), поэтому сервер теста — под установленный клиент:
/// есть клиент 16 — обычный образ тестов, иначе <c>pgvector/pgvector:pg&lt;версия клиента&gt;</c>. Без клиента или Docker
/// тест падает с подсказкой, а не пропускается.
/// </remarks>
[Trait("Category", "Backup")]
public sealed class BackupRoundTripTests : IAsyncLifetime
{
    private const string Database = "isc_ai_backup_test";
    private const string KeyPassword = "пароль хранителя ключа";

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder(ImageForLocalClient()).Build();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "iscai-backup-it", Guid.NewGuid().ToString("N"));
    private NpgsqlConnectionStringBuilder _app = null!;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        var server = new NpgsqlConnectionStringBuilder(_postgres.GetConnectionString());
        await using (var admin = new NpgsqlConnection(server.ConnectionString))
        {
            await admin.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE {Database}", admin);
            await create.ExecuteNonQueryAsync();
        }

        // Без пула: EF с pgvector держит свой пул, который ClearAllPools не закрывает, — а «остановленный хост» перед
        // восстановлением не должен держать ни одного соединения.
        _app = new NpgsqlConnectionStringBuilder(server.ConnectionString) { Database = Database, Pooling = false };
        await using var db = new CoreContextFactory(_app.ConnectionString).CreateDbContext();
        await db.Database.MigrateAsync();
        Directory.CreateDirectory(_dir);
    }

    public async Task DisposeAsync()
    {
        NpgsqlConnection.ClearAllPools();
        await _postgres.DisposeAsync();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // Временный каталог — не повод валить прогон.
        }
    }

    [Fact(DisplayName = "ADR-0063: копия → проверка манифеста → восстановление программой двумя лицами → прежнее сохранено, восстановление в журнале")]
    public async Task Backup_verify_restore_round_trip()
    {
        // Данные: строки в базе (с кириллицей) и файлы хранилища.
        await ExecuteAsync(_app, "CREATE TABLE public.backup_probe (id int PRIMARY KEY, text text NOT NULL); INSERT INTO public.backup_probe VALUES (1, 'салам дүйнө'), (2, 'материалы дела');");
        var storage = Path.Combine(_dir, "storage");
        WriteFile(storage, "media-originals/5/a.bin", RandomNumberGenerator.GetBytes(300_000));
        WriteFile(storage, "docflow/Справка.txt", Encoding.UTF8.GetBytes("текст справки"));
        var docflow = Path.Combine(_dir, "docflow-files");
        WriteFile(docflow, "documents/2026/10/приказ.docx", Encoding.UTF8.GetBytes("текст приказа"));

        // Ключи хранителя — программой восстановления, как на рабочем месте хранителя.
        var keys = Path.Combine(_dir, "keys");
        (await RestoreApp.RunAsync(["keygen", "--out", keys], new ScriptedConsole(KeyPassword, KeyPassword))).ShouldBe(0);
        using var publicKey = BackupKeys.LoadPublicKey(Path.Combine(keys, RestoreApp.PublicKeyFile));

        var target = Path.Combine(_dir, "medium");
        Directory.CreateDirectory(target);
        var settings = BackupSettings.From(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Storage:BasePath"] = storage,
            ["Backup:TargetFolder"] = target,
            ["Backup:PublicKey:Path"] = Path.Combine(keys, RestoreApp.PublicKeyFile),
            ["Backup:PublicKey:Sha256"] = BackupKeys.FingerprintHex(publicKey),
        }).Build(), [new BackupDatabase("core", _app.ConnectionString)], [new BackupFileRoot("docflow", docflow)]);
        var contexts = new CoreContextFactory(_app.ConnectionString);
        var service = new BackupService(contexts, settings, new BackupJobChannel(), new AuditWriter(contexts), TimeProvider.System,
            NullLogger<BackupService>.Instance);

        // Состояние готово к копии; копия — одна за раз.
        var overview = await service.GetOverviewAsync();
        overview.Problems.ShouldBeEmpty(string.Join(" ", overview.Problems));
        overview.Ready.ShouldBeTrue();
        var started = await service.StartAsync(7);
        var runId = started.RunId.ShouldNotBeNull(started.Refusal);
        (await service.StartAsync(7)).Refusal.ShouldBe("Копия уже делается — дождитесь её окончания.");

        await service.RunCopyAsync(runId, 7, CancellationToken.None);
        var run = (await service.GetOverviewAsync()).Runs.Single(r => r.Id == runId);
        run.Status.ShouldBe(BackupRunStatus.Succeeded, run.Error);
        (run.Databases, run.StorageFiles).ShouldBe((1, 3));
        var folder = Path.Combine(target, run.FolderName!);
        Directory.GetFiles(folder).Select(Path.GetFileName).Order().ShouldBe(
            [$"db-{Database}.dump.enc", "files-core.tar.enc", "files-docflow.tar.enc", BackupManifest.FileName]);
        File.ReadAllBytes(Path.Combine(folder, "files-core.tar.enc")).AsSpan().IndexOf("текст справки"u8).ShouldBe(-1, "в копии нет открытого текста");
        File.ReadAllBytes(Path.Combine(folder, "files-docflow.tar.enc")).AsSpan().IndexOf("текст приказа"u8).ShouldBe(-1, "в копии нет открытого текста");

        (await service.VerifyLatestAsync(7)).RunId.ShouldBe(runId);
        await service.RunVerifyAsync(runId, 7, CancellationToken.None);
        (await service.GetOverviewAsync()).Runs.Single(r => r.Id == runId).VerifyStatus.ShouldBe(BackupVerifyStatus.Ok);

        await using (var db = contexts.CreateDbContext())
        {
            (await db.AuditRecords.CountAsync(a => a.ObjectRef == $"core:backup:{runId}" && a.Action == AuditAction.Export)).ShouldBe(1);
        }

        // После копии данные меняются — восстановление должно вернуть состояние копии.
        await ExecuteAsync(_app, "DELETE FROM public.backup_probe WHERE id = 2;");
        File.Delete(Path.Combine(storage, "docflow", "Справка.txt"));
        File.Delete(Path.Combine(docflow, "documents", "2026", "10", "приказ.docx"));

        // Восстановление программой: хост «остановлен» — соединений нет (пул выключен).
        var server = new NpgsqlConnectionStringBuilder(_app.ConnectionString) { Database = null };
        var console = new ScriptedConsole("Ашыров Б. Д.", "Садыков К. А.", KeyPassword, RestoreApp.ConfirmWord);
        // Хранилище ядра — туда, где было при копии (по манифесту); документооборот — явным --files-docflow.
        string[] restore =
        [
            "restore", "--backup", folder, "--server", server.ConnectionString, "--files-docflow", docflow,
            "--key", Path.Combine(keys, RestoreApp.PrivateKeyFile),
        ];

        // Хост «запущен» (держит отметку присутствия), хотя соединений с базой нет, — отказ до подтверждения.
        using (HostPresence.TryHold(storage).ShouldNotBeNull())
        {
            var running = new ScriptedConsole("Ашыров Б. Д.", "Садыков К. А.", KeyPassword);
            (await RestoreApp.RunAsync(restore, running)).ShouldBe(1, running.Output);
            running.Output.ShouldContain("Система запущена");
            running.Output.ShouldNotContain("Введите " + RestoreApp.ConfirmWord);
        }

        var code = await RestoreApp.RunAsync(restore, console);
        code.ShouldBe(0, console.Output);

        (await ScalarAsync<long>(_app, "SELECT count(*) FROM public.backup_probe")).ShouldBe(2);
        File.ReadAllText(Path.Combine(storage, "docflow", "Справка.txt")).ShouldBe("текст справки");
        File.ReadAllText(Path.Combine(docflow, "documents", "2026", "10", "приказ.docx")).ShouldBe("текст приказа");

        // Ничего не уничтожено: прежние база и хранилище — под новыми именами.
        var previous = await ScalarAsync<string>(new NpgsqlConnectionStringBuilder(_app.ConnectionString) { Database = "postgres" },
            $"SELECT datname FROM pg_database WHERE datname LIKE '{Database}_before_restore_%'");
        previous.ShouldNotBeNull();
        (await ScalarAsync<long>(new NpgsqlConnectionStringBuilder(_app.ConnectionString) { Database = previous }, "SELECT count(*) FROM public.backup_probe")).ShouldBe(1);
        Directory.GetDirectories(_dir, "storage.before-restore-*").ShouldHaveSingleItem();
        Directory.GetDirectories(_dir, "docflow-files.before-restore-*").ShouldHaveSingleItem();

        // «Старт хоста»: отметка о восстановлении — в журнал аудита поимённо, затем в done.
        NpgsqlConnection.ClearAllPools();
        var restored = new BackupService(new CoreContextFactory(_app.ConnectionString), settings, new BackupJobChannel(),
            new AuditWriter(new CoreContextFactory(_app.ConnectionString)), TimeProvider.System, NullLogger<BackupService>.Instance);
        (await restored.ProcessRestoreMarkersAsync()).ShouldBe(1);
        (await restored.ProcessRestoreMarkersAsync()).ShouldBe(0);
        await using (var db = new CoreContextFactory(_app.ConnectionString).CreateDbContext())
        {
            var entry = await db.AuditRecords.SingleAsync(a => a.ObjectRef == "core:backup:restore");
            entry.Action.ShouldBe(AuditAction.Modify);
            entry.PayloadSensitive.ShouldNotBeNull().ShouldContain("docflow-files.before-restore-");
        }

        // Восстановленная база — из копии: прогон в ней ещё «идёт» (копия делалась во время него) — старт хоста его закроет.
        (await restored.RecoverAfterRestartAsync()).ShouldBeGreaterThanOrEqualTo(1);
    }

    // Образ сервера под установленный клиент PostgreSQL: 16 — как у остальных тестов; иначе самый новый клиент.
    private static string ImageForLocalClient()
    {
        var exe = OperatingSystem.IsWindows() ? "pg_dump.exe" : "pg_dump";
        var majors = PgTools.Installs().Where(i => File.Exists(Path.Combine(i.Bin, exe))).Select(i => i.Major).ToList();
        return majors.Count == 0 || majors.Contains(16) ? TestPostgres.Image : $"pgvector/pgvector:pg{majors.Max()}";
    }

    private static async Task ExecuteAsync(NpgsqlConnectionStringBuilder connection, string sql)
    {
        await using var db = new NpgsqlConnection(connection.ConnectionString);
        await db.OpenAsync();
        await using var command = new NpgsqlCommand(sql, db);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<T?> ScalarAsync<T>(NpgsqlConnectionStringBuilder connection, string sql)
    {
        await using var db = new NpgsqlConnection(connection.ConnectionString);
        await db.OpenAsync();
        await using var command = new NpgsqlCommand(sql, db);
        return (T?)await command.ExecuteScalarAsync();
    }

    private static void WriteFile(string root, string relative, byte[] content)
    {
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
    }

    /// <summary>Консоль со сценарием ответов.</summary>
    private sealed class ScriptedConsole(params string[] answers) : IRestoreConsole
    {
        private readonly Queue<string> _answers = new(answers);
        private readonly StringBuilder _output = new();

        public string Output => _output.ToString();

        public void WriteLine(string text = "") => _output.AppendLine(text);

        public string? Ask(string prompt)
        {
            _output.Append(prompt);
            return _answers.Count > 0 ? _answers.Dequeue() : null;
        }

        public string? AskSecret(string prompt) => Ask(prompt);
    }
}
