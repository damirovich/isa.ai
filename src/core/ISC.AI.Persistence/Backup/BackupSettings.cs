using System.Globalization;
using ISC.AI.Abstractions.Backup;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace ISC.AI.Persistence.Backup;

/// <summary>
/// Настройки резервных копий (секция <c>Backup</c>, ADR-0063) и что копировать — базы, которые зарегистрировали слои
/// данных поставки (<see cref="BackupDatabase"/>), и каталоги файлов: хранилище ядра и зарегистрированные пакетами
/// (<see cref="BackupFileRoot"/>).
/// </summary>
/// <param name="TargetFolder">Каталог на изолированном носителе (<c>Backup:TargetFolder</c>).</param>
/// <param name="PublicKeyPath">PEM открытого ключа Заказчика (<c>Backup:PublicKey:Path</c>).</param>
/// <param name="PublicKeySha256">Пин отпечатка ключа (<c>Backup:PublicKey:Sha256</c>).</param>
/// <param name="PgDumpPath">Каталог или путь к <c>pg_dump</c> (<c>Backup:PgDumpPath</c>); пусто — искать.</param>
/// <param name="ReminderDays">Через сколько дней без успешной копии напоминать (<c>Backup:ReminderDays</c>).</param>
/// <param name="FileRoots">Каталоги файлов: первым — хранилище ядра (<c>Storage:BasePath</c>), затем пакетов.</param>
/// <param name="Databases">Базы данных — по одной на сервер, порт и имя.</param>
public sealed record BackupSettings(
    string? TargetFolder,
    string? PublicKeyPath,
    string? PublicKeySha256,
    string? PgDumpPath,
    int ReminderDays,
    IReadOnlyList<BackupFileRoot> FileRoots,
    IReadOnlyList<NpgsqlConnectionStringBuilder> Databases)
{
    /// <summary>Напоминание по умолчанию — 30 дней (ТБ-082: не реже раза в месяц).</summary>
    public const int DefaultReminderDays = 30;

    /// <summary>Корень хранилища ядра — в нём отметки о восстановлении (их переносит в журнал хост при запуске).</summary>
    public string StorageRoot => FileRoots.First(r => r.Name == BackupFileRoot.CoreName).Path;

    /// <summary>
    /// Читает настройки из конфигурации хоста. <paramref name="sources"/> — базы, которые зарегистрировали слои данных
    /// поставки (строки подключения других профилей из общего файла настроек сюда не попадают); одинаковые сервер, порт и
    /// база — одна копия. <paramref name="packageRoots"/> — каталоги файлов пакетов.
    /// </summary>
    public static BackupSettings From(
        IConfiguration configuration, IEnumerable<BackupDatabase> sources, IEnumerable<BackupFileRoot>? packageRoots = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(sources);
        var databases = new List<NpgsqlConnectionStringBuilder>();
        foreach (var source in sources)
        {
            var builder = new NpgsqlConnectionStringBuilder(source.ConnectionString);
            if (!databases.Any(d => string.Equals(d.Host, builder.Host, StringComparison.OrdinalIgnoreCase)
                    && d.Port == builder.Port && string.Equals(d.Database, builder.Database, StringComparison.Ordinal)))
            {
                databases.Add(builder);
            }
        }

        var storage = configuration["Storage:BasePath"];
        var roots = new List<BackupFileRoot>
        {
            new(BackupFileRoot.CoreName, string.IsNullOrWhiteSpace(storage) ? Storage.LocalFileStorage.DefaultBasePath : storage),
        };
        roots.AddRange(packageRoots ?? []);
        return new BackupSettings(
            Text(configuration["Backup:TargetFolder"]),
            Text(configuration["Backup:PublicKey:Path"]),
            Text(configuration["Backup:PublicKey:Sha256"]),
            Text(configuration["Backup:PgDumpPath"]),
            int.TryParse(configuration["Backup:ReminderDays"], NumberStyles.Integer, CultureInfo.InvariantCulture, out var days) && days is >= 1 and <= 365
                ? days
                : DefaultReminderDays,
            roots.Select(r => r with { Path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(r.Path)) }).Distinct().ToList(),
            databases);
    }

    /// <summary>Что не настроено — пусто, если копию можно делать (носитель проверяется отдельно).</summary>
    public IReadOnlyList<string> Problems()
    {
        var problems = new List<string>();
        if (TargetFolder is null)
        {
            problems.Add("Не задан каталог носителя (Backup:TargetFolder).");
        }

        if (PublicKeyPath is null)
        {
            problems.Add("Не задан открытый ключ Заказчика (Backup:PublicKey:Path).");
        }
        else if (!File.Exists(PublicKeyPath))
        {
            problems.Add($"Нет файла открытого ключа: {Path.GetFullPath(PublicKeyPath)}.");
        }

        if (PublicKeySha256 is null)
        {
            problems.Add("Не задан пин ключа (Backup:PublicKey:Sha256) — без него ключ не используется.");
        }

        if (Databases.Count == 0)
        {
            problems.Add("Нет баз для копии: ни один слой данных не зарегистрировал свою базу (BackupDatabase).");
        }

        // Каталоги файлов: имя — в имени файла копии и ключе программы восстановления; один каталог внутри другого попал
        // бы в копию дважды, а при восстановлении подмена внешнего унесла бы внутренний.
        foreach (var root in FileRoots)
        {
            if (!BackupFileRoot.IsValidName(root.Name))
            {
                problems.Add($"Каталог файлов «{root.Name}» ({root.Path}): имя для копии — латиница в нижнем регистре, цифры и «-».");
            }
            else if (FileRoots.Count(r => r.Name == root.Name) > 1)
            {
                problems.Add($"Каталог файлов «{root.Name}» зарегистрирован дважды с разными путями.");
            }
        }

        for (var i = 0; i < FileRoots.Count; i++)
        {
            for (var j = i + 1; j < FileRoots.Count; j++)
            {
                if (Overlap(FileRoots[i].Path, FileRoots[j].Path))
                {
                    problems.Add($"Каталоги файлов «{FileRoots[i].Name}» ({FileRoots[i].Path}) и «{FileRoots[j].Name}» ({FileRoots[j].Path}) "
                        + "совпадают или вложены один в другой — задайте раздельные каталоги.");
                }
            }
        }

        return problems;
    }

    private static string? Text(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool Overlap(string a, string b) =>
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase)
        || a.StartsWith(b + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
        || b.StartsWith(a + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}
