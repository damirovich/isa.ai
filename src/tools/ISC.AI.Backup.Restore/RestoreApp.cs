using System.Globalization;
using System.Security.Cryptography;
using ISC.AI.Abstractions.Backup;
using Npgsql;

namespace ISC.AI.Backup.Restore;

/// <summary>Ошибка аргументов командной строки — код выхода 2 и подсказка.</summary>
public sealed class RestoreUsageException(string message) : Exception(message);

/// <summary>
/// Программа восстановления из резервной копии (ТФ-АДМ-04, ТБ-082, ADR-0063). Команды:
/// <c>keygen</c> — пара ключей хранителя; <c>verify</c> — манифест копии; <c>restore</c> — восстановление баз и хранилища.
/// </summary>
/// <remarks>
/// ИНВАРИАНТЫ: восстановление — двумя лицами (Администратор и хранитель ключа, имена обязательны и различны, оба — в
/// журнал аудита через отметку в хранилище); закрытый ключ и пароли — только в памяти процесса; к базе не должно быть
/// других подключений (хост остановлен); ничего не уничтожается — прежние база и хранилище переименовываются и остаются
/// для отката; подтверждение — словом <see cref="ConfirmWord"/>.
/// </remarks>
public static class RestoreApp
{
    /// <summary>Слово подтверждения восстановления.</summary>
    public const string ConfirmWord = "ВОССТАНОВИТЬ";

    /// <summary>Наименьшая длина пароля закрытого ключа.</summary>
    public const int MinPasswordLength = 12;

    /// <summary>Имя файла открытого ключа, который создаёт <c>keygen</c>.</summary>
    public const string PublicKeyFile = "backup-public.pem";

    /// <summary>Имя файла закрытого ключа, который создаёт <c>keygen</c>.</summary>
    public const string PrivateKeyFile = "backup-private.pem";

    /// <summary>Начало ключа, задающего каталог восстановления каталога файлов копии: <c>--files-&lt;имя&gt;</c>.</summary>
    public const string FilesOptionPrefix = "--files-";

    /// <summary>Сколько ждать, пока сервер закроет сеансы только что остановленного хоста.</summary>
    private static readonly TimeSpan ConnectionsGrace = TimeSpan.FromSeconds(10);

    /// <summary>Как часто перепроверять сеансы в течение ожидания.</summary>
    private static readonly TimeSpan ConnectionsPoll = TimeSpan.FromMilliseconds(500);

    /// <summary>Строка помощи.</summary>
    public const string Usage =
        "ISC.AI.Backup.Restore keygen --out <каталог>\n"
        + "    пара ключей резервных копий для хранителя ключа: закрытый — под паролем, открытый и отпечаток — Администратору\n"
        + "ISC.AI.Backup.Restore verify --backup <каталог копии> [--fingerprint <SHA-256 ключа>]\n"
        + "    проверка манифеста копии без закрытого ключа\n"
        + "ISC.AI.Backup.Restore restore --backup <каталог копии> --server \"Host=..;Port=..;Username=..\"\n"
        + "                      [--files-<имя> <каталог>]... [--key <закрытый ключ>] [--pg-bin <каталог bin PostgreSQL>]\n"
        + "    восстановление при ОСТАНОВЛЕННОМ хосте, двумя лицами; прежние базы и каталоги файлов сохраняются для отката;\n"
        + "    каталоги файлов (core — хранилище ядра, docflow — документооборот, …) — туда, где были при копии, или по --files-<имя>";

    /// <summary>Выполняет команду и возвращает код выхода: 0 — успех, 1 — сбой или отказ, 2 — неверные аргументы.</summary>
    public static async Task<int> RunAsync(IReadOnlyList<string> args, IRestoreConsole console, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(console);
        try
        {
            if (args.Count == 0 || args[0] is "help" or "--help" or "-h")
            {
                console.WriteLine(Usage);
                return args.Count == 0 ? 2 : 0;
            }

            var options = ParseOptions(args.Skip(1).ToList());
            return args[0] switch
            {
                "keygen" => await KeygenAsync(options, console, cancellationToken),
                "verify" => await VerifyAsync(options, console, cancellationToken),
                "restore" => await RestoreAsync(options, console, cancellationToken),
                _ => throw new RestoreUsageException($"Неизвестная команда «{args[0]}»."),
            };
        }
        catch (RestoreUsageException exception)
        {
            console.WriteLine(exception.Message);
            console.WriteLine(Usage);
            return 2;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException
                                              or InvalidDataException or CryptographicException or NpgsqlException)
        {
            console.WriteLine("Ошибка: " + exception.Message);
            return 1;
        }
    }

    private static async Task<int> KeygenAsync(Dictionary<string, string> options, IRestoreConsole console, CancellationToken cancellationToken)
    {
        var folder = Required(options, "--out");
        Directory.CreateDirectory(folder);
        var publicPath = Path.Combine(folder, PublicKeyFile);
        var privatePath = Path.Combine(folder, PrivateKeyFile);
        if (File.Exists(publicPath) || File.Exists(privatePath))
        {
            console.WriteLine($"В каталоге {Path.GetFullPath(folder)} уже есть ключи — новые не создаются (старые ключи нужны для старых копий).");
            return 1;
        }

        var password = console.AskSecret($"Пароль закрытого ключа (не короче {MinPasswordLength} символов): ") ?? string.Empty;
        if (password.Length < MinPasswordLength)
        {
            console.WriteLine($"Пароль короче {MinPasswordLength} символов — ключи не созданы.");
            return 1;
        }

        if (console.AskSecret("Повторите пароль: ") != password)
        {
            console.WriteLine("Пароли не совпали — ключи не созданы.");
            return 1;
        }

        using var key = BackupKeys.Generate();
        await File.WriteAllTextAsync(privatePath, BackupKeys.ExportPrivatePem(key, password), cancellationToken);
        await File.WriteAllTextAsync(publicPath, BackupKeys.ExportPublicPem(key), cancellationToken);
        console.WriteLine($"Ключи созданы в {Path.GetFullPath(folder)}:");
        console.WriteLine($"  {PublicKeyFile} — открытый: передать Администратору (Backup:PublicKey:Path);");
        console.WriteLine($"  {PrivateKeyFile} — закрытый, под паролем: хранить у хранителя ключа, НЕ на сервере системы.");
        console.WriteLine("Отпечаток для Backup:PublicKey:Sha256:");
        console.WriteLine("  " + BackupKeys.FingerprintHex(key));
        return 0;
    }

    private static async Task<int> VerifyAsync(Dictionary<string, string> options, IRestoreConsole console, CancellationToken cancellationToken)
    {
        var folder = Required(options, "--backup");
        options.TryGetValue("--fingerprint", out var fingerprint);
        console.WriteLine($"Проверка копии {Path.GetFullPath(folder)}…");
        var check = await BackupVerifier.VerifyAsync(folder, fingerprint, cancellationToken);
        if (check.Manifest is { } manifest)
        {
            Describe(manifest, console);
        }

        if (check.Ok)
        {
            console.WriteLine("Копия цела: все файлы на месте, размеры и SHA-256 совпадают с манифестом.");
            return 0;
        }

        console.WriteLine("Копия НЕ цела:");
        foreach (var problem in check.Problems)
        {
            console.WriteLine("  — " + problem);
        }

        return 1;
    }

    private static async Task<int> RestoreAsync(Dictionary<string, string> options, IRestoreConsole console, CancellationToken cancellationToken)
    {
        var folder = Path.GetFullPath(Required(options, "--backup"));
        var server = new NpgsqlConnectionStringBuilder(Required(options, "--server"));

        // 1. Копия цела — до любых вопросов и до ключа.
        console.WriteLine($"Шаг 1. Проверка копии {folder}…");
        var check = await BackupVerifier.VerifyAsync(folder, null, cancellationToken);
        if (!check.Ok)
        {
            console.WriteLine("Копия НЕ цела — восстановление невозможно:");
            foreach (var problem in check.Problems)
            {
                console.WriteLine("  — " + problem);
            }

            return 1;
        }

        var manifest = check.Manifest!;
        Describe(manifest, console);

        // Куда восстанавливать каталоги файлов: --files-<имя> или туда, где каталог был при копии.
        var targets = StorageTargets(manifest, options);

        // 2. Два лица — поимённо (ТБ-082).
        console.WriteLine("Шаг 2. Участники восстановления (оба попадут в журнал аудита).");
        var administrator = (console.Ask("ФИО Администратора: ") ?? string.Empty).Trim();
        var custodian = (console.Ask("ФИО хранителя ключа: ") ?? string.Empty).Trim();
        if (administrator.Length < 3 || custodian.Length < 3)
        {
            console.WriteLine("Нужны ФИО обоих участников — восстановление отменено.");
            return 1;
        }

        if (string.Equals(administrator, custodian, StringComparison.OrdinalIgnoreCase))
        {
            console.WriteLine("Администратор и хранитель ключа — разные люди (процедура двух лиц) — восстановление отменено.");
            return 1;
        }

        // 3. Закрытый ключ — только в памяти; отпечаток должен совпасть с манифестом.
        console.WriteLine("Шаг 3. Закрытый ключ (вводит хранитель).");
        var keyPath = options.TryGetValue("--key", out var givenKey) ? givenKey : (console.Ask("Файл закрытого ключа: ") ?? string.Empty).Trim();
        using var key = BackupKeys.LoadPrivateKey(keyPath, console.AskSecret("Пароль ключа: ") ?? string.Empty);
        if (!string.Equals(BackupKeys.FingerprintHex(key), manifest.KeyFingerprint, StringComparison.OrdinalIgnoreCase))
        {
            console.WriteLine("Ключ не подходит: копия зашифрована другим ключом — восстановление отменено.");
            return 1;
        }

        if (string.IsNullOrEmpty(server.Password))
        {
            server.Password = console.AskSecret($"Пароль PostgreSQL пользователя {server.Username}: ") ?? string.Empty;
        }

        // 4. Хост остановлен: к базам никто не подключён.
        console.WriteLine("Шаг 4. Проверка, что система остановлена…");
        if (!await SystemStoppedAsync(server, manifest, targets, console, cancellationToken))
        {
            return 1;
        }

        // pg_restore той же версии, что сервер, — до подтверждения: не найден — отказ, на сервере ничего не создано.
        var tools = new PgTools(options.GetValueOrDefault("--pg-bin"));
        if (manifest.Databases.Count > 0)
        {
            var restoreProgram = await tools.ForServerAsync("pg_restore", BackupRestorer.Maintenance(server, manifest.Databases[0].Name), cancellationToken);
            console.WriteLine("  клиент: " + await PgTools.VersionAsync(restoreProgram, cancellationToken));
        }

        // 5. Подтверждение словом.
        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        console.WriteLine();
        console.WriteLine($"Будет восстановлено на сервер {server.Host}:{server.Port}:");
        foreach (var database in manifest.Databases)
        {
            console.WriteLine($"  база «{database.Name}» — прежняя сохранится как «{database.Name}_before_restore_{stamp}»;");
        }

        foreach (var (entry, target) in targets)
        {
            console.WriteLine($"  файлы «{entry.Name}» в {target} — прежние сохранятся как {target}.before-restore-{stamp};");
        }

        if (console.Ask($"Введите {ConfirmWord} для подтверждения: ")?.Trim() != ConfirmWord)
        {
            console.WriteLine("Не подтверждено — ничего не изменено.");
            return 1;
        }

        // 6. Восстановление в новые имена (рабочее не тронуто), затем подмена.
        var restorer = new BackupRestorer(tools);
        foreach (var database in manifest.Databases)
        {
            console.WriteLine($"Шаг 5. База «{database.Name}»: расшифровка и pg_restore в «{database.Name}_restore_{stamp}»…");
            await restorer.RestoreDatabaseAsync(server, Path.Combine(folder, database.File), key, $"{database.Name}_restore_{stamp}", cancellationToken);
        }

        foreach (var (entry, target) in targets)
        {
            var unpacked = $"{target}.restore-{stamp}";
            console.WriteLine($"Шаг 6. Файлы «{entry.Name}»: расшифровка и распаковка в {unpacked}…");
            var files = await BackupRestorer.RestoreStorageAsync(Path.Combine(folder, entry.File), key, unpacked, cancellationToken);
            console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  файлов {files.Files}, {files.Bytes} байт."));
        }

        if (!await SystemStoppedAsync(server, manifest, targets, console, cancellationToken))
        {
            console.WriteLine($"Подмена не выполнена; восстановленное лежит в «*_restore_{stamp}» и «*.restore-{stamp}» — удалите или повторите.");
            return 1;
        }

        // 7. Подмена. Каталоги — первыми: переименование папки может не пройти (папка открыта в другой программе, антивирус),
        // а базы к этому моменту не тронуты. Любой сбой — откат сделанного в обратном порядке: рабочее остаётся прежним
        // целиком, а не наполовину восстановленным.
        console.WriteLine("Шаг 7. Подмена рабочих каталогов файлов и баз…");
        var swappedStorages = new List<(string Target, string? Previous)>();
        var swappedDatabases = new List<(string Name, string? Previous)>();
        try
        {
            foreach (var (_, target) in targets)
            {
                var previous = $"{target}.before-restore-{stamp}";
                var had = BackupRestorer.SwapStorage(target, $"{target}.restore-{stamp}", previous);
                swappedStorages.Add((target, had ? previous : null));
            }

            foreach (var database in manifest.Databases)
            {
                var previous = $"{database.Name}_before_restore_{stamp}";
                var had = await BackupRestorer.SwapDatabaseAsync(server, database.Name, $"{database.Name}_restore_{stamp}", previous, cancellationToken);
                swappedDatabases.Add((database.Name, had ? previous : null));
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NpgsqlException)
        {
            console.WriteLine("Подмена не удалась: " + exception.Message);
            await RollBackAsync(server, stamp, swappedStorages, swappedDatabases, console);
            return 1;
        }

        var previousDatabases = swappedDatabases.Where(d => d.Previous is not null).Select(d => d.Previous!).ToList();
        var previousStorages = swappedStorages.Where(s => s.Previous is not null).Select(s => s.Previous!).ToList();

        // Отметка — в хранилище ядра: его читает хост при запуске (нет его в копии — в первый каталог). Не записалась —
        // восстановление сделано, но в журнал аудита само не попадёт (ТБ-082): об этом — громко и кодом 1.
        var exitCode = 0;
        var journal = targets.FirstOrDefault(t => t.Entry.Name == BackupFileRoot.CoreName).Target ?? targets.FirstOrDefault().Target;
        if (journal is not null)
        {
            try
            {
                await new RestoreMarker(
                    DateTimeOffset.Now, Path.GetFileName(folder), manifest.CreatedAt, administrator, custodian,
                    manifest.Databases.Select(d => d.Name).ToList(), previousDatabases, previousStorages)
                    .SaveAsync(journal, cancellationToken);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                console.WriteLine($"ВНИМАНИЕ: восстановление выполнено, но отметка для журнала аудита не записана ({exception.Message}). "
                    + $"Запишите восстановление в журнал вручную: копия {Path.GetFileName(folder)}, Администратор — {administrator}, "
                    + $"хранитель ключа — {custodian}.");
                exitCode = 1;
            }
        }

        console.WriteLine();
        console.WriteLine("Восстановление завершено. Запустите систему — при запуске она запишет восстановление в журнал аудита.");
        if (previousDatabases.Count > 0 || previousStorages.Count > 0)
        {
            console.WriteLine("Прежние данные сохранены для отката; после проверки их удаляет Администратор:");
            foreach (var previous in previousDatabases)
            {
                console.WriteLine("  база " + previous);
            }

            foreach (var previous in previousStorages)
            {
                console.WriteLine("  каталог " + previous);
            }
        }

        return exitCode;
    }

    // Откат подмены в обратном порядке (базы, затем каталоги). Не вышло — точное состояние на экран: что где лежит и что
    // вернуть вручную; запускать систему в смешанном состоянии нельзя.
    private static async Task RollBackAsync(
        NpgsqlConnectionStringBuilder server, string stamp, List<(string Target, string? Previous)> storages,
        List<(string Name, string? Previous)> databases, IRestoreConsole console)
    {
        var failed = new List<string>();
        foreach (var (name, previous) in Enumerable.Reverse(databases))
        {
            try
            {
                await BackupRestorer.UndoSwapDatabaseAsync(server, name, $"{name}_restore_{stamp}", previous, CancellationToken.None);
            }
            catch (Exception exception) when (exception is NpgsqlException or InvalidOperationException)
            {
                failed.Add($"база «{name}» — сейчас восстановленная, прежняя — «{previous ?? "(её не было)"}»: {exception.Message}");
            }
        }

        foreach (var (target, previous) in Enumerable.Reverse(storages))
        {
            try
            {
                BackupRestorer.UndoSwapStorage(target, $"{target}.restore-{stamp}", previous);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                failed.Add($"каталог {target} — сейчас восстановленный, прежний — {previous ?? "(его не было)"}: {exception.Message}");
            }
        }

        if (failed.Count == 0)
        {
            console.WriteLine($"Откат выполнен: рабочие базы и каталоги — прежние. Восстановленное лежит в «*_restore_{stamp}» и "
                + $"«*.restore-{stamp}» — устраните причину и повторите восстановление (или удалите их).");
            return;
        }

        console.WriteLine("ВНИМАНИЕ: откат выполнен не полностью — система в смешанном состоянии, НЕ запускайте её. Верните вручную:");
        foreach (var line in failed)
        {
            console.WriteLine("  — " + line);
        }
    }

    // Система остановлена: отметка присутствия хоста свободна (хост держит её всё время работы) и к базам никто не подключён.
    private static async Task<bool> SystemStoppedAsync(
        NpgsqlConnectionStringBuilder server, BackupManifest manifest, List<(BackupStorageEntry Entry, string Target)> targets,
        IRestoreConsole console, CancellationToken cancellationToken)
    {
        foreach (var (_, target) in targets)
        {
            if (HostPresence.IsHeld(target))
            {
                console.WriteLine($"Система запущена: её отметка {Path.Combine(target, StorageArchive.RestoreJournalFolder, HostPresence.FileName)} "
                    + "занята. Остановите систему (хост) и повторите.");
                return false;
            }
        }

        return await NoOtherConnectionsAsync(server, manifest, console, cancellationToken);
    }

    private static async Task<bool> NoOtherConnectionsAsync(
        NpgsqlConnectionStringBuilder server, BackupManifest manifest, IRestoreConsole console, CancellationToken cancellationToken)
    {
        foreach (var database in manifest.Databases)
        {
            // Сразу после остановки хоста сервер ещё закрывает его сеансы — даём им немного времени, прежде чем отказать.
            var others = await BackupRestorer.OtherConnectionsAsync(server, database.Name, cancellationToken);
            for (var waited = TimeSpan.Zero; others > 0 && waited < ConnectionsGrace; waited += ConnectionsPoll)
            {
                await Task.Delay(ConnectionsPoll, cancellationToken);
                others = await BackupRestorer.OtherConnectionsAsync(server, database.Name, cancellationToken);
            }

            if (others > 0)
            {
                console.WriteLine($"К базе «{database.Name}» подключено сеансов: {others}. Остановите систему (хост) и повторите.");
                return false;
            }
        }

        return true;
    }

    // Каталог восстановления для каждого каталога файлов копии: --files-<имя> или путь из манифеста. Неизвестное имя в
    // --files-* и два каталога в одном месте (или один внутри другого) — ошибка аргументов: подмена затёрла бы соседа.
    private static List<(BackupStorageEntry Entry, string Target)> StorageTargets(BackupManifest manifest, Dictionary<string, string> options)
    {
        foreach (var name in options.Keys.Where(k => k.StartsWith(FilesOptionPrefix, StringComparison.Ordinal)))
        {
            if (manifest.Storages.All(s => FilesOptionPrefix + s.Name != name))
            {
                throw new RestoreUsageException(
                    $"В копии нет каталога файлов «{name[FilesOptionPrefix.Length..]}» (есть: {string.Join(", ", manifest.Storages.Select(s => s.Name))}).");
            }
        }

        var targets = manifest.Storages
            .Select(s => (Entry: s, Target: Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.GetValueOrDefault(FilesOptionPrefix + s.Name) ?? s.Root))))
            .ToList();
        foreach (var (entry, target) in targets)
        {
            var clash = targets.FirstOrDefault(other => !ReferenceEquals(other.Entry, entry) && IsSameOrInside(target, other.Target));
            if (clash.Entry is not null)
            {
                throw new RestoreUsageException(
                    $"Каталоги файлов «{entry.Name}» ({target}) и «{clash.Entry.Name}» ({clash.Target}) совпадают или вложены — укажите разные через --files-<имя>.");
            }
        }

        return targets;
    }

    private static bool IsSameOrInside(string path, string root) =>
        string.Equals(path, root, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static void Describe(BackupManifest manifest, IRestoreConsole console)
    {
        console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"Копия от {manifest.CreatedAt:dd.MM.yyyy HH:mm}, система {manifest.SystemVersion}, ключ {manifest.KeyFingerprint[..16]}…"));
        foreach (var database in manifest.Databases)
        {
            console.WriteLine($"  база «{database.Name}» ({database.Host}:{database.Port}, PostgreSQL {database.ServerVersion})");
        }

        foreach (var storage in manifest.Storages)
        {
            console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  файлы «{storage.Name}» ({storage.Root}): файлов {storage.Files}, {storage.Bytes} байт"));
        }
    }

    private static Dictionary<string, string> ParseOptions(List<string> args)
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Count; i++)
        {
            var name = args[i];
            if (!name.StartsWith("--", StringComparison.Ordinal))
            {
                throw new RestoreUsageException($"Ожидался ключ, получено «{name}».");
            }

            if (i + 1 >= args.Count || args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                throw new RestoreUsageException($"Для ключа {name} не задано значение.");
            }

            if (!options.TryAdd(name, args[++i]))
            {
                throw new RestoreUsageException($"Ключ {name} задан дважды.");
            }
        }

        return options;
    }

    private static string Required(Dictionary<string, string> options, string name) =>
        options.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new RestoreUsageException($"Не задан обязательный ключ {name}.");
}
