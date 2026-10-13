using System.Globalization;
using System.Text;
using ISC.AI.Modules.Media.Application.Features.Assets;
using ISC.AI.Modules.Media.Domain.Model;

namespace ISC.AI.Modules.Media.Application.Features.Import;

/// <summary>
/// Обзор папки архива для экрана импорта (ADR-0041): вложенные папки и черновик описи. Скрытые и системные элементы,
/// символические ссылки и точки соединения не показываются и не обходятся — те же правила, что у приёма файлов
/// (<see cref="MediaImportPaths"/>).
/// </summary>
public static class MediaImportFolders
{
    /// <summary>Сколько вложенных папок показывается.</summary>
    public const int MaxSubfolders = 500;

    private static EnumerationOptions Options(bool recurse) => new()
    {
        RecurseSubdirectories = recurse,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint,
        ReturnSpecialDirectories = false,
    };

    /// <summary>Имена вложенных папок по алфавиту.</summary>
    public static (IReadOnlyList<string> Names, bool Truncated) ListSubfolders(string folder)
    {
        ArgumentNullException.ThrowIfNull(folder);

        var names = new DirectoryInfo(folder)
            .EnumerateDirectories("*", Options(recurse: false))
            .Select(d => d.Name)
            .Take(MaxSubfolders + 1)
            .ToList();
        var truncated = names.Count > MaxSubfolders;
        return (names.Take(MaxSubfolders).Order(StringComparer.CurrentCultureIgnoreCase).ToList(), truncated);
    }

    /// <summary>
    /// Строки черновика описи: все файлы допустимых форматов под папкой, по пути. Реквизиты пустые; «дело» —
    /// имя папки первого уровня, если оператор так выбрал.
    /// </summary>
    /// <param name="folder">Папка архива (проверена <see cref="MediaImportPaths.ResolveFolder"/>).</param>
    /// <param name="caseFromFirstFolder">Заполнить «дело» именем папки первого уровня.</param>
    /// <param name="cancellationToken">Отмена.</param>
    public static (IReadOnlyList<ManifestRow> Rows, bool Truncated) ListFiles(
        string folder, bool caseFromFirstFolder, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(folder);

        var paths = new List<string>();
        var truncated = false;
        foreach (var file in new DirectoryInfo(folder).EnumerateFiles("*", Options(recurse: true)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (MediaFileRules.ContentTypeForFile(file.Name) is null)
            {
                continue;
            }

            if (paths.Count >= ImportManifest.MaxRows)
            {
                truncated = true;
                break;
            }

            paths.Add(Path.GetRelativePath(folder, file.FullName).Replace(Path.DirectorySeparatorChar, '/'));
        }

        paths.Sort(MediaImportPaths.PathComparer);
        var rows = new List<ManifestRow>(paths.Count);
        for (var i = 0; i < paths.Count; i++)
        {
            var path = paths[i];
            var slash = path.IndexOf('/', StringComparison.Ordinal);
            var caseNumber = caseFromFirstFolder && slash > 0 ? path[..slash] : null;
            rows.Add(new ManifestRow(i + 2, path, caseNumber));
        }

        return (rows, truncated);
    }
}

/// <summary>Отчёт задания импорта в CSV (ADR-0041 п. 8) — для акта переноса и разбора отказов.</summary>
public static class MediaImportReport
{
    /// <summary>Столбцы отчёта.</summary>
    public static IReadOnlyList<string> Columns { get; } =
    [
        "строка описи", "файл", "дело", "состояние", "причина", "носитель", "sha256 до переноса", "sha256 после переноса",
        "обработка", "размер, байт",
    ];

    /// <summary>Дата и время в местном поясе — как в описи: «дд.мм.гггг чч:мм:сс».</summary>
    public static string FormatLocal(DateTimeOffset value, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        return TimeZoneInfo.ConvertTime(value, zone).ToString("dd.MM.yyyy HH:mm:ss", CultureInfo.InvariantCulture);
    }

    /// <summary>Подпись состояния строки.</summary>
    public static string StatusLabel(MediaImportItemStatus status) => status switch
    {
        MediaImportItemStatus.Pending => "ждёт приёма",
        MediaImportItemStatus.InProgress => "принимается",
        MediaImportItemStatus.Accepted => "принят",
        MediaImportItemStatus.Duplicate => "дубликат",
        MediaImportItemStatus.Rejected => "отказ",
        _ => status.ToString(),
    };

    /// <summary>Подпись подачи в обработку.</summary>
    public static string ProcessingLabel(MediaImportProcessingState state) => state switch
    {
        MediaImportProcessingState.Waiting => "ждёт очереди",
        MediaImportProcessingState.Queued => "в обработке",
        MediaImportProcessingState.Done => "обработан",
        _ => string.Empty,
    };

    /// <summary>Записывает отчёт: UTF-8 с BOM, разделитель «;».</summary>
    public static async Task WriteAsync(
        Stream output, IAsyncEnumerable<MediaImportItemView> items, Action? counted = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(items);

        await using var writer = new StreamWriter(output, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), leaveOpen: true);
        await writer.WriteLineAsync(string.Join(';', Columns.Select(ImportManifest.EscapeCsv)));
        await foreach (var item in items.WithCancellation(cancellationToken))
        {
            counted?.Invoke();
            await writer.WriteLineAsync(string.Join(';', new[]
            {
                item.RowNumber.ToString(CultureInfo.InvariantCulture),
                item.RelativePath,
                item.CaseNumber,
                StatusLabel(item.Status),
                item.Reason,
                item.AssetId?.ToString(CultureInfo.InvariantCulture),
                item.ExpectedSha256,
                item.ContentSha256,
                ProcessingLabel(item.Processing),
                item.ByteSize.ToString(CultureInfo.InvariantCulture),
            }.Select(ImportManifest.EscapeCsv)));
        }

        await writer.FlushAsync(cancellationToken);
    }
}
