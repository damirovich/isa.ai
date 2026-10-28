using System.Diagnostics;
using System.Globalization;
using System.Text;
using Npgsql;

namespace ISC.AI.Backup;

/// <summary>
/// Клиентские программы PostgreSQL (<c>pg_dump</c>, <c>pg_restore</c>) для резервных копий (ADR-0063). Где искать —
/// <c>Backup:PgDumpPath</c> (каталог <c>bin</c> или путь к <c>pg_dump</c>; если задан — только там); не задано —
/// установка той же версии, что сервер (<c>Program Files\PostgreSQL\&lt;версия&gt;</c>, <c>/usr/lib/postgresql/&lt;версия&gt;</c>),
/// затем <c>PATH</c>, затем остальные установки.
/// </summary>
/// <remarks>
/// <para>ИНВАРИАНТ (ТБ-013): пароль базы передаётся процессу переменной окружения <c>PGPASSWORD</c> и не попадает ни в
/// командную строку (её видно в списке процессов), ни в тексты ошибок.</para>
/// <para>ИНВАРИАНТ (ТБ-082 — копия должна восстанавливаться): основная версия клиента равна версии сервера. Клиент новее
/// сервера пишет в копию команды, которых этот сервер не знает (PostgreSQL 17+ — <c>SET transaction_timeout</c>), и
/// копия на него не восстанавливается; клиент старше сервера копию не снимет. Несовпадение — отказ до начала работы.</para>
/// </remarks>
public sealed class PgTools(string? configuredPath)
{
    private const int MaxStderrChars = 4000;

    /// <summary>
    /// Путь к какому-нибудь установленному <c>pg_dump</c> — есть ли клиент вообще (готовность без обращения к серверу);
    /// не найден — <see cref="FileNotFoundException"/> с подсказкой. Для работы с сервером — <see cref="ForServerAsync"/>.
    /// </summary>
    public string DumpPath => Candidates("pg_dump", serverMajor: 0).FirstOrDefault() ?? throw NotInstalled("pg_dump");

    /// <summary>
    /// Программа <paramref name="tool"/> (<c>pg_dump</c>, <c>pg_restore</c>) той же основной версии, что сервер
    /// <paramref name="connection"/>. Нет такой — <see cref="InvalidOperationException"/> со списком найденных и подсказкой;
    /// нет никакой — <see cref="FileNotFoundException"/>.
    /// </summary>
    public async Task<string> ForServerAsync(string tool, NpgsqlConnectionStringBuilder connection, CancellationToken cancellationToken = default)
    {
        var serverMajor = MajorVersion(await ServerVersionAsync(connection, cancellationToken).ConfigureAwait(false));
        var found = new List<string>();
        foreach (var candidate in Candidates(tool, serverMajor))
        {
            var version = await VersionAsync(candidate, cancellationToken).ConfigureAwait(false);
            if (MajorVersion(version) == serverMajor)
            {
                return candidate;
            }

            found.Add($"{candidate} ({version})");
        }

        if (found.Count == 0)
        {
            throw NotInstalled(tool);
        }

        var major = serverMajor.ToString(CultureInfo.InvariantCulture);
        throw new InvalidOperationException(
            $"Нет {tool} версии {major} — под сервер PostgreSQL {major}. Найдены: {string.Join("; ", found)}. "
            + "Клиент другой версии не годится: копия, снятая клиентом новее сервера, на этот сервер не восстанавливается, "
            + $"а клиент старше сервера копию не снимет. Установите клиент PostgreSQL {major} и укажите его каталог bin "
            + "в Backup:PgDumpPath (программе восстановления — --pg-bin).");
    }

    /// <summary>
    /// Основная версия PostgreSQL из строки версии: «pg_dump (PostgreSQL) 16.4» и «16.4 (Debian 16.4-1)» → 16;
    /// не разобрать — 0.
    /// </summary>
    public static int MajorVersion(string? version)
    {
        var digits = new string((version ?? string.Empty).SkipWhile(c => !char.IsAsciiDigit(c)).TakeWhile(char.IsAsciiDigit).ToArray());
        return int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var major) ? major : 0;
    }

    /// <summary>Версия программы (вывод <c>--version</c>).</summary>
    public static async Task<string> VersionAsync(string program, CancellationToken cancellationToken = default)
    {
        var info = new ProcessStartInfo(program, "--version")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var process = Process.Start(info) ?? throw new InvalidOperationException($"Не удалось запустить {program}.");
        var output = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return output.Trim();
    }

    /// <summary>Версия сервера PostgreSQL (<c>SHOW server_version</c>).</summary>
    /// <remarks>
    /// Без пула: соединение закрывается сразу — иначе оно осталось бы висеть на базе, которую программа восстановления
    /// затем переименовывает (переименование невозможно, пока к базе кто-то подключён).
    /// </remarks>
    public static async Task<string> ServerVersionAsync(NpgsqlConnectionStringBuilder connection, CancellationToken cancellationToken = default)
    {
        await using var db = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(connection.ConnectionString) { Pooling = false }.ConnectionString);
        await db.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand("SHOW server_version", db);
        return (string)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? string.Empty);
    }

    /// <summary>Объём базы, байт (<c>pg_database_size</c>) — для оценки размера копии.</summary>
    public static async Task<long> DatabaseSizeAsync(NpgsqlConnectionStringBuilder connection, CancellationToken cancellationToken = default)
    {
        await using var db = new NpgsqlConnection(connection.ConnectionString);
        await db.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand("SELECT pg_database_size(current_database())", db);
        return (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? 0L);
    }

    /// <summary>
    /// Копия базы в формате <c>custom</c>: вывод <c>pg_dump</c> потоком отдаётся <paramref name="consume"/> (шифрование
    /// и запись на носитель) — на диск открытым текстом не ложится.
    /// </summary>
    public async Task DumpAsync(NpgsqlConnectionStringBuilder connection, Func<Stream, Task> consume, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(consume);
        var program = await ForServerAsync("pg_dump", connection, cancellationToken).ConfigureAwait(false);
        var info = StartInfo(program, connection, connection.Database!);
        info.ArgumentList.Add("--format=custom");
        info.ArgumentList.Add("--no-password");
        info.RedirectStandardOutput = true;

        using var process = new Process { StartInfo = info };
        var stderr = CaptureErrors(process);
        process.Start();
        process.BeginErrorReadLine();
        using var kill = cancellationToken.Register(() => TryKill(process));
        try
        {
            await consume(process.StandardOutput.BaseStream).ConfigureAwait(false);
        }
        catch
        {
            // Шифрование или запись на носитель упали (кончилось место) — вывод больше никто не читает, и pg_dump навсегда
            // повис бы на полном канале, а прогон — в «идёт». Останавливаем его, причина — в исключении.
            TryKill(process);
            throw;
        }
        finally
        {
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                string.Create(CultureInfo.InvariantCulture, $"pg_dump базы «{connection.Database}» завершился с кодом {process.ExitCode}: {Tail(stderr)}"));
        }
    }

    /// <summary>Восстанавливает копию базы (формат <c>custom</c>) из потока в базу <paramref name="database"/>.</summary>
    public async Task RestoreAsync(
        NpgsqlConnectionStringBuilder connection, string database, Func<Stream, Task> produce, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(produce);
        var program = await ForServerAsync("pg_restore", new NpgsqlConnectionStringBuilder(connection.ConnectionString) { Database = database }, cancellationToken)
            .ConfigureAwait(false);
        var info = StartInfo(program, connection, database);
        info.ArgumentList.Add("--exit-on-error");
        info.ArgumentList.Add("--no-password");
        info.RedirectStandardInput = true;
        info.RedirectStandardOutput = true;

        using var process = new Process { StartInfo = info };
        var stderr = CaptureErrors(process);
        process.Start();
        process.BeginErrorReadLine();
        process.BeginOutputReadLine();
        using var kill = cancellationToken.Register(() => TryKill(process));
        try
        {
            await using (var input = process.StandardInput.BaseStream)
            {
                await produce(input).ConfigureAwait(false);
            }
        }
        catch (IOException) when (process.HasExited)
        {
            // pg_restore оборвал чтение (ошибка в данных) — причина в его коде выхода и stderr ниже.
        }
        catch
        {
            // Расшифровка упала (подменённый или битый файл) — недовосстановленная база pg_restore не нужна.
            TryKill(process);
            throw;
        }
        finally
        {
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                string.Create(CultureInfo.InvariantCulture, $"pg_restore в базу «{database}» завершился с кодом {process.ExitCode}: {Tail(stderr)}"));
        }
    }

    private static ProcessStartInfo StartInfo(string program, NpgsqlConnectionStringBuilder connection, string database)
    {
        var info = new ProcessStartInfo(program)
        {
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardErrorEncoding = Encoding.UTF8,
        };
        info.ArgumentList.Add("--host=" + (connection.Host ?? "localhost"));
        info.ArgumentList.Add("--port=" + connection.Port.ToString(CultureInfo.InvariantCulture));
        if (!string.IsNullOrEmpty(connection.Username))
        {
            info.ArgumentList.Add("--username=" + connection.Username);
        }

        info.ArgumentList.Add("--dbname=" + database);
        info.Environment["PGPASSWORD"] = connection.Password ?? string.Empty;
        info.Environment["PGCLIENTENCODING"] = "UTF8";

        // Сообщения самой программы — без перевода: переведённые она пишет в кодировке Windows (cp1251), и в журнале
        // копий они превращаются в «кракозябры»; английские — ASCII, читаются при любой кодировке.
        info.Environment["LC_MESSAGES"] = "C";
        if (connection.SslMode != SslMode.Disable)
        {
            info.Environment["PGSSLMODE"] = connection.SslMode switch
            {
                SslMode.Require => "require",
                SslMode.VerifyCA => "verify-ca",
                SslMode.VerifyFull => "verify-full",
                _ => "prefer",
            };
        }

        return info;
    }

    private static StringBuilder CaptureErrors(Process process)
    {
        var stderr = new StringBuilder();
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is { } line)
            {
                lock (stderr)
                {
                    stderr.AppendLine(line);
                    if (stderr.Length > MaxStderrChars * 2)
                    {
                        stderr.Remove(0, stderr.Length - MaxStderrChars);
                    }
                }
            }
        };
        return stderr;
    }

    private static string Tail(StringBuilder stderr)
    {
        lock (stderr)
        {
            var text = stderr.ToString().Trim();
            return text.Length <= MaxStderrChars ? text : "…" + text[^MaxStderrChars..];
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // Процесс уже завершился.
        }
    }

    // Кандидаты по порядку: заданный путь (если задан — только он; нет файла — FileNotFoundException), установка версии
    // сервера, PATH, остальные установки от новых к старым. Повторы и несуществующие файлы пропускаются.
    private IEnumerable<string> Candidates(string tool, int serverMajor)
    {
        var exe = OperatingSystem.IsWindows() ? tool + ".exe" : tool;
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            var configured = Path.GetFullPath(configuredPath.Trim());
            var candidate = Directory.Exists(configured)
                ? Path.Combine(configured, exe)
                : Path.Combine(Path.GetDirectoryName(configured) ?? string.Empty, exe);
            yield return File.Exists(candidate)
                ? candidate
                : throw new FileNotFoundException(
                    $"Не найден {exe} по пути из Backup:PgDumpPath ({candidate}). Укажите каталог bin клиента PostgreSQL.", candidate);
            yield break;
        }

        var installs = Installs().ToList();
        var fromPath = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(folder => folder.Trim());
        var folders = installs.Where(i => i.Major == serverMajor).Select(i => i.Bin)
            .Concat(fromPath)
            .Concat(installs.Select(i => i.Bin));
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in folders)
        {
            var candidate = Path.Combine(folder, exe);
            if (File.Exists(candidate) && seen.Add(Path.GetFullPath(candidate)))
            {
                yield return candidate;
            }
        }
    }

    // Установки PostgreSQL по основной версии, от новых к старым: Windows — Program Files\PostgreSQL\<версия>\bin,
    // Linux (Debian/Ubuntu) — /usr/lib/postgresql/<версия>/bin.
    internal static IEnumerable<(int Major, string Bin)> Installs()
    {
        var root = OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PostgreSQL")
            : "/usr/lib/postgresql";
        if (!Directory.Exists(root))
        {
            return [];
        }

        return Directory.GetDirectories(root)
            .Select(dir => (Major: int.TryParse(Path.GetFileName(dir), NumberStyles.None, CultureInfo.InvariantCulture, out var v) ? v : 0,
                Bin: Path.Combine(dir, "bin")))
            .Where(i => i.Major > 0)
            .OrderByDescending(i => i.Major);
    }

    private static FileNotFoundException NotInstalled(string tool) =>
        new($"Не найден {tool}: клиент PostgreSQL не установлен или не указан. Задайте Backup:PgDumpPath (каталог bin PostgreSQL).", tool);
}
