using System.Globalization;
using ISC.AI.Modules.Media.Application.Features.Import;
using Microsoft.Extensions.Configuration;

namespace ISC.AI.Modules.Media.Application;

/// <summary>Каталог сервера, откуда разрешено брать архив, и почему он не используется (если так).</summary>
/// <param name="Path">Полный путь каталога.</param>
/// <param name="Problem">Почему каталог исключён; <see langword="null"/> — используется.</param>
public sealed record MediaImportRoot(string Path, string? Problem = null)
{
    /// <summary>Каталог используется.</summary>
    public bool IsUsable => Problem is null;
}

/// <summary>
/// Настройки пакетного импорта архива (ADR-0041): каталоги сервера, откуда разрешено брать архив, и сколько носителей
/// архива одновременно стоит в общей очереди обработки.
/// </summary>
/// <param name="Roots">Каталоги из настройки — используемые и исключённые с причиной.</param>
/// <param name="ProcessingSlots">Сколько носителей архива одновременно подано в общую очередь обработки.</param>
public sealed record MediaImportOptions(IReadOnlyList<MediaImportRoot> Roots, int ProcessingSlots)
{
    /// <summary>Ключ списка каталогов архива (массив или строка через «;»).</summary>
    public const string RootsKey = "Media:Import:Roots";

    /// <summary>Ключ числа носителей архива в общей очереди обработки.</summary>
    public const string ProcessingSlotsKey = "Media:Import:ProcessingSlots";

    /// <summary>По умолчанию в очереди один носитель архива: новые материалы ждут не дольше его обработки.</summary>
    public const int DefaultProcessingSlots = 1;

    /// <summary>Верхняя граница: больше — архив снова вытеснял бы новые материалы.</summary>
    public const int MaxProcessingSlots = 8;

    /// <summary>Ключ корня хранилища ядра (<c>LocalFileStorage</c>) — каталог импорта не может его пересекать.</summary>
    public const string StorageBasePathKey = "Storage:BasePath";

    /// <summary>Корень хранилища по умолчанию — как у <c>LocalFileStorage</c>.</summary>
    public const string DefaultStorageBasePath = "storage-files";

    /// <summary>Без настройки каталогов импорт выключен.</summary>
    public static MediaImportOptions Default { get; } = new([], DefaultProcessingSlots);

    /// <summary>Используемые каталоги.</summary>
    public IReadOnlyList<string> UsableRoots => Roots.Where(r => r.IsUsable).Select(r => r.Path).ToList();

    /// <summary>
    /// Читает настройку. Каталог исключается с причиной, если путь не абсолютный или пересекается с хранилищем
    /// материалов или временным каталогом: иначе через импорт можно было бы «перенести» в своё дело чужой материал
    /// из хранилища (там файлы всех дел и грифов) или временную копию чужой обработки.
    /// </summary>
    public static MediaImportOptions Read(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(RootsKey);
        var configured = section.GetChildren().Select(c => c.Value).ToList();
        if (configured.Count == 0 && section.Value is { } single)
        {
            configured = single.Split(';').Select(s => (string?)s).ToList();
        }

        var storage = configuration[StorageBasePathKey];
        var forbidden = new[]
        {
            (Path: FullPath(string.IsNullOrWhiteSpace(storage) ? DefaultStorageBasePath : storage), Name: "хранилищем материалов"),
            (Path: FullPath(Path.GetTempPath()), Name: "временным каталогом"),
        };

        var roots = new List<MediaImportRoot>();
        foreach (var value in configured)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            var raw = value.Trim();
            if (!Path.IsPathFullyQualified(raw))
            {
                roots.Add(new MediaImportRoot(raw, "путь не абсолютный"));
                continue;
            }

            var full = FullPath(raw);
            var clash = forbidden.FirstOrDefault(f => MediaImportPaths.Overlaps(full, f.Path));
            roots.Add(clash.Path is null
                ? new MediaImportRoot(full)
                : new MediaImportRoot(full, "пересекается с " + clash.Name));
        }

        var slots = int.TryParse(configuration[ProcessingSlotsKey], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? Math.Clamp(parsed, 1, MaxProcessingSlots)
            : DefaultProcessingSlots;

        return new MediaImportOptions(roots.DistinctBy(r => r.Path, MediaImportPaths.PathComparer).ToList(), slots);
    }

    private static string FullPath(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
}
