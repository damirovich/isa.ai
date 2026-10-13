namespace ISC.AI.Modules.Media.Application.Features.Import;

/// <summary>Файл архива, найденный по пути описи.</summary>
/// <param name="FullPath">Полный путь на диске сервера.</param>
/// <param name="RelativePath">Путь относительно папки задания, разделитель «/».</param>
/// <param name="Length">Размер, байт.</param>
public sealed record MediaImportSourceFile(string FullPath, string RelativePath, long Length);

/// <summary>
/// Безопасное разрешение путей импорта архива (ADR-0041 п. 1): сервер читает файлы ТОЛЬКО внутри каталогов из
/// настройки <see cref="MediaImportOptions.RootsKey"/>, и никакая строка описи не выводит за них.
/// </summary>
/// <remarks>
/// <para>
/// ИНВАРИАНТ БЕЗОПАСНОСТИ. Путь описи — относительный к папке задания. Отклоняются: абсолютный путь, диск, сетевой
/// путь, сегмент <c>..</c>, недопустимые символы, символическая ссылка или точка соединения (junction) на ЛЮБОМ
/// участке пути ниже каталога из настройки, скрытый или системный файл. Последнее проверяется по атрибутам каждого
/// каталога пути: ссылка внутри архива могла бы указывать в хранилище материалов, и файл другого дела и грифа «переехал»
/// бы в дело оператора. Сравнение «под каталогом» — с разделителем на конце: соседний каталог с тем же префиксом
/// (<c>archive</c> и <c>archive-old</c>) не считается вложенным.
/// </para>
/// <para>
/// Экземпляр помнит уже проверенные каталоги: в описи сотни тысяч строк из одних и тех же папок, и атрибуты каждой
/// папки читаются один раз за проверку.
/// </para>
/// </remarks>
public sealed class MediaImportPaths
{
    /// <summary>Сообщение: путь выводит за папку задания или не относительный.</summary>
    public const string OutsideMessage = "путь должен быть относительным и не выходить за папку архива";

    /// <summary>Сообщение: ссылка или точка соединения в пути.</summary>
    public const string ReparsePointMessage = "в пути есть символическая ссылка или точка соединения — такие пути не принимаются";

    /// <summary>Сообщение: файла нет.</summary>
    public const string NotFoundMessage = "файл не найден";

    /// <summary>Сообщение: скрытый или системный файл.</summary>
    public const string HiddenMessage = "скрытый или системный файл не принимается";

    private readonly Dictionary<string, bool> _safeDirectories = new(PathComparer);

    /// <summary>Сравнение путей файловой системы: на Windows без учёта регистра.</summary>
    public static StringComparer PathComparer { get; } =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary><paramref name="path"/> совпадает с <paramref name="root"/> или лежит внутри него (пути — полные).</summary>
    public static bool IsUnder(string path, string root)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(root);

        var normalizedPath = Path.TrimEndingDirectorySeparator(path);
        var normalizedRoot = Path.TrimEndingDirectorySeparator(root);
        return string.Equals(normalizedPath, normalizedRoot, PathComparison)
            || normalizedPath.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, PathComparison);
    }

    /// <summary>Один каталог вложен в другой или они совпадают.</summary>
    public static bool Overlaps(string first, string second) => IsUnder(first, second) || IsUnder(second, first);

    /// <summary>
    /// Нормализует относительный путь описи: разделители — «/», без ведущих и повторных разделителей и сегментов «.».
    /// <see langword="null"/> — путь недопустим (абсолютный, с диском, с «..», с недопустимыми символами).
    /// </summary>
    public static string? NormalizeRelative(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return null;
        }

        var text = relativePath.Trim();

        // Абсолютный путь, диск («C:…», «C:file» — относительный к текущему каталогу диска) и сетевой путь.
        if (Path.IsPathRooted(text) || text.Contains(':', StringComparison.Ordinal) || text.StartsWith("//", StringComparison.Ordinal)
            || text.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return null;
        }

        var invalid = Path.GetInvalidFileNameChars();
        var segments = new List<string>();
        foreach (var raw in text.Split('/', '\\'))
        {
            var segment = raw.Trim();
            if (segment.Length == 0 || segment == ".")
            {
                continue;
            }

            // Выход выше папки запрещён целиком, даже если путь потом возвращается обратно: проще и надёжнее.
            if (segment == ".." || segment.IndexOfAny(invalid) >= 0)
            {
                return null;
            }

            segments.Add(segment);
        }

        return segments.Count == 0 ? null : string.Join('/', segments);
    }

    /// <summary>
    /// Папка задания внутри каталога из настройки: <paramref name="root"/> — один из используемых каталогов,
    /// <paramref name="relativeFolder"/> — вложенная папка (пусто — сам каталог). Папка должна существовать, а путь к
    /// ней — не содержать ссылок.
    /// </summary>
    /// <returns>Полный путь папки или <see langword="null"/> с причиной в <paramref name="problem"/>.</returns>
    public string? ResolveFolder(IReadOnlyList<string> usableRoots, string root, string? relativeFolder, out string? problem)
    {
        ArgumentNullException.ThrowIfNull(usableRoots);

        var configuredRoot = usableRoots.FirstOrDefault(r => string.Equals(r, root, PathComparison));
        if (configuredRoot is null)
        {
            problem = "каталог не входит в список разрешённых для импорта (" + MediaImportOptions.RootsKey + ")";
            return null;
        }

        var folder = configuredRoot;
        if (!string.IsNullOrWhiteSpace(relativeFolder))
        {
            var normalized = NormalizeRelative(relativeFolder);
            if (normalized is null)
            {
                problem = "папка: " + OutsideMessage;
                return null;
            }

            folder = Path.GetFullPath(Path.Combine(configuredRoot, normalized.Replace('/', Path.DirectorySeparatorChar)));
            if (!IsUnder(folder, configuredRoot))
            {
                problem = "папка: " + OutsideMessage;
                return null;
            }
        }

        if (!Directory.Exists(folder))
        {
            problem = "папка не найдена";
            return null;
        }

        if (!IsChainSafe(configuredRoot, folder))
        {
            problem = "папка: " + ReparsePointMessage;
            return null;
        }

        problem = null;
        return Path.TrimEndingDirectorySeparator(folder);
    }

    /// <summary>
    /// Файл описи внутри папки задания. Возвращает файл с размером или <see langword="null"/> с причиной.
    /// </summary>
    /// <param name="folder">Полный путь папки задания (уже проверенной <see cref="ResolveFolder"/>).</param>
    /// <param name="relativePath">Путь из описи.</param>
    /// <param name="problem">Причина отказа.</param>
    public MediaImportSourceFile? ResolveFile(string folder, string? relativePath, out string? problem)
    {
        ArgumentNullException.ThrowIfNull(folder);

        var normalized = NormalizeRelative(relativePath);
        if (normalized is null)
        {
            problem = OutsideMessage;
            return null;
        }

        var full = Path.GetFullPath(Path.Combine(folder, normalized.Replace('/', Path.DirectorySeparatorChar)));
        if (!IsUnder(full, folder) || string.Equals(Path.TrimEndingDirectorySeparator(full), folder, PathComparison))
        {
            problem = OutsideMessage;
            return null;
        }

        FileInfo info;
        try
        {
            info = new FileInfo(full);
            if (!info.Exists)
            {
                problem = Directory.Exists(full) ? "это папка, а не файл" : NotFoundMessage;
                return null;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            problem = "файл недоступен: " + exception.Message;
            return null;
        }

        if (info.Attributes.HasFlag(FileAttributes.ReparsePoint) || info.LinkTarget is not null)
        {
            problem = ReparsePointMessage;
            return null;
        }

        if (info.Attributes.HasFlag(FileAttributes.Hidden) || info.Attributes.HasFlag(FileAttributes.System))
        {
            problem = HiddenMessage;
            return null;
        }

        if (!IsChainSafe(folder, info.DirectoryName ?? folder))
        {
            problem = ReparsePointMessage;
            return null;
        }

        problem = null;
        return new MediaImportSourceFile(info.FullName, normalized, info.Length);
    }

    // Каждый каталог от base (не включая — его проверили раньше или он из настройки) до target включительно — не
    // ссылка и не точка соединения.
    private bool IsChainSafe(string baseFolder, string target)
    {
        var current = Path.TrimEndingDirectorySeparator(Path.GetFullPath(target));
        var stop = Path.TrimEndingDirectorySeparator(baseFolder);
        var visited = new List<string>();
        var safe = true;
        while (!string.Equals(current, stop, PathComparison))
        {
            if (_safeDirectories.TryGetValue(current, out var known))
            {
                safe = known;
                break;
            }

            visited.Add(current);
            try
            {
                var info = new DirectoryInfo(current);
                if (!info.Exists || info.Attributes.HasFlag(FileAttributes.ReparsePoint) || info.LinkTarget is not null)
                {
                    safe = false;
                    break;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                safe = false;
                break;
            }

            var parent = Path.GetDirectoryName(current);
            if (parent is null || !IsUnder(parent, stop))
            {
                safe = false;
                break;
            }

            current = Path.TrimEndingDirectorySeparator(parent);
        }

        foreach (var directory in visited)
        {
            _safeDirectories[directory] = safe;
        }

        return safe;
    }
}
