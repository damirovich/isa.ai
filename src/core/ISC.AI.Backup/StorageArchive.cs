using System.Formats.Tar;

namespace ISC.AI.Backup;

/// <summary>Итог упаковки или распаковки хранилища.</summary>
/// <param name="Files">Число файлов.</param>
/// <param name="Bytes">Объём файлов, байт.</param>
public sealed record StorageArchiveResult(int Files, long Bytes);

/// <summary>
/// Файловое хранилище в архив tar и обратно (ADR-0063) — потоком, средствами <c>System.Formats.Tar</c>. Пути в архиве —
/// относительные, через «/». При распаковке путь, выходящий за каталог назначения, — отказ: подложенная копия не
/// запишет файл мимо хранилища.
/// </summary>
public static class StorageArchive
{
    /// <summary>
    /// Каталог отметок о восстановлении в хранилище (их пишет программа восстановления, читает хост) — в копию не идёт.
    /// </summary>
    public const string RestoreJournalFolder = "_restore-journal";

    /// <summary>Упаковывает каталог в поток; файлы, исчезнувшие во время копии, пропускаются.</summary>
    public static async Task<StorageArchiveResult> WriteAsync(string root, Stream output, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(output);

        var files = 0;
        var bytes = 0L;
        await using var tar = new TarWriter(output, TarEntryFormat.Pax, leaveOpen: true);
        if (!Directory.Exists(root))
        {
            return new StorageArchiveResult(0, 0);
        }

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            IgnoreInaccessible = false,
        };
        foreach (var path in Directory.EnumerateFiles(root, "*", options).Order(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
            if (relative.StartsWith(RestoreJournalFolder + "/", StringComparison.Ordinal))
            {
                continue;
            }

            FileStream content;
            try
            {
                content = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.SequentialScan);
            }
            catch (FileNotFoundException)
            {
                continue; // удалён между перечислением и чтением (уничтожение по ADR-0025)
            }
            catch (DirectoryNotFoundException)
            {
                continue;
            }

            await using (content)
            {
                var entry = new PaxTarEntry(TarEntryType.RegularFile, relative) { DataStream = content };
                await tar.WriteEntryAsync(entry, cancellationToken).ConfigureAwait(false);
                files++;
                bytes += content.Length;
            }
        }

        return new StorageArchiveResult(files, bytes);
    }

    /// <summary>Распаковывает архив в каталог (создаётся при необходимости).</summary>
    /// <exception cref="InvalidDataException">В архиве путь за пределы каталога назначения.</exception>
    public static async Task<StorageArchiveResult> ExtractAsync(Stream input, string destination, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination));
        Directory.CreateDirectory(root);
        var files = 0;
        var bytes = 0L;
        await using var tar = new TarReader(input, leaveOpen: true);
        while (await tar.GetNextEntryAsync(copyData: false, cancellationToken).ConfigureAwait(false) is { } entry)
        {
            if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile))
            {
                continue; // каталоги создаются по путям файлов; ссылок и устройств в копии хранилища быть не должно
            }

            var target = Path.GetFullPath(Path.Combine(root, entry.Name));
            if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"Архив хранилища повреждён или подложен: путь «{entry.Name}» выходит за каталог.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using var file = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16);
            if (entry.DataStream is { } data)
            {
                await data.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
            }

            files++;
            bytes += file.Length;
        }

        return new StorageArchiveResult(files, bytes);
    }
}
