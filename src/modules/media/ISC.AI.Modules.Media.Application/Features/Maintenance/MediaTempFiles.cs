using System;
using System.Collections.Generic;
using System.IO;
using ISC.AI.Modules.Media.Domain.Model;
using Microsoft.Extensions.Logging;

namespace ISC.AI.Modules.Media.Application.Features.Maintenance;

/// <summary>
/// Временные копии материалов дела, которые конвейеры пакета «Медиа» кладут на диск для внешних процессов
/// (ffmpeg, распознаватель речи работают с путём, а не с потоком): управляемый каталог
/// <c>%TEMP%\iscai-media</c> и его уборка при старте хоста.
/// </summary>
/// <remarks>
/// <para>
/// ГАРАНТИРОВАННОЕ УДАЛЕНИЕ (ТБ-064, ADR-0025). Копия — это полный исходник носителя (запись разговора, видео).
/// Конвейер удаляет её в <c>finally</c>, но при аварийной остановке хоста (снятие процесса, отключение питания)
/// <c>finally</c> не выполняется, и копия пережила бы и перезапуск, и уничтожение носителя или дела. Поэтому
/// копии лежат в ОДНОМ известном каталоге, а не россыпью в корне %TEMP%, и при старте хоста — до запуска фоновой
/// очереди, когда живых прогонов в этом процессе ещё нет, — всё, что там осталось, удаляется
/// (<see cref="SweepStale"/>). До следующего старта оставшийся файл удаляется вручную по пути из журнала.
/// </para>
/// <para>
/// Корень настраивается конструктором — тесты работают в своём каталоге и не трогают настоящий %TEMP%.
/// Имя копии — префикс конвейера, GUID и БЕЗОПАСНОЕ расширение (<see cref="MediaFileNames.SafeExtension"/>):
/// путь уходит внешнему процессу текстом командной строки.
/// </para>
/// </remarks>
public sealed class MediaTempFiles
{
    /// <summary>Имя управляемого каталога временных копий внутри %TEMP%.</summary>
    public const string FolderName = "iscai-media";

    /// <summary>Префикс копии для конвейера расшифровки речи (ADR-0026).</summary>
    public const string SpeechPrefix = "speech-";

    /// <summary>Префикс копии для конвейера индексации лиц (раскадровка, ТО-мат-06).</summary>
    public const string FramesPrefix = "frames-";

    /// <summary>
    /// Префикс копии оригинала видео для снимка кадра (ADR-0028) — только когда хранилище не даёт локального
    /// пути (<c>ILocalFileLocator</c> не зарегистрирован или вернул <see langword="null"/>).
    /// </summary>
    public const string SnapshotPrefix = "snapshot-";

    /// <summary>
    /// Шаблоны файлов пакета в САМОМ корне %TEMP%: копии конвейеров прежних версий (<c>isc-speech-*</c>,
    /// <c>isc-media-*</c> — до управляемого каталога) и буфер приёма файла хранилищем (<c>iscai-media-*</c>,
    /// хеш считается до записи в хранилище). Их тоже подбирает уборка при старте.
    /// </summary>
    public static IReadOnlyList<string> RootFilePatterns { get; } = ["isc-speech-*", "isc-media-*", "iscai-media-*"];

    /// <summary>Каталог по умолчанию: <c>%TEMP%\iscai-media</c>; прежние файлы — в корне %TEMP%.</summary>
    public MediaTempFiles()
        : this(Path.Combine(Path.GetTempPath(), FolderName), Path.GetTempPath())
    {
    }

    /// <summary>Каталог копий <paramref name="root"/> и корень, где искать прежние файлы, — <paramref name="legacyRoot"/>.</summary>
    public MediaTempFiles(string root, string legacyRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(legacyRoot);
        Root = Path.GetFullPath(root);
        LegacyRoot = Path.GetFullPath(legacyRoot);
    }

    /// <summary>Управляемый каталог временных копий.</summary>
    public string Root { get; }

    /// <summary>Каталог, в корне которого лежат файлы по шаблонам <see cref="RootFilePatterns"/> (%TEMP%).</summary>
    public string LegacyRoot { get; }

    /// <summary>
    /// Путь для новой временной копии: <c>{Root}\{префикс}{GUID}{расширение}</c>. Каталог создаётся при
    /// необходимости; файл — нет (его пишет вызывающий и удаляет в <c>finally</c>).
    /// </summary>
    /// <param name="prefix">Префикс конвейера (<see cref="SpeechPrefix"/>, <see cref="FramesPrefix"/>, <see cref="SnapshotPrefix"/>).</param>
    /// <param name="storedFileName">Имя исходника в хранилище — только ради расширения (после фильтра).</param>
    public string NewPath(string prefix, string? storedFileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        Directory.CreateDirectory(Root);
        return Path.Combine(Root, prefix + Guid.NewGuid().ToString("N") + MediaFileNames.SafeExtension(storedFileName));
    }

    /// <summary>
    /// Удаляет оставшиеся от прежних запусков файлы: всё в <see cref="Root"/> и файлы по шаблонам
    /// <see cref="RootFilePatterns"/> в <see cref="LegacyRoot"/>, последний раз записанные РАНЬШЕ
    /// <paramref name="olderThanUtc"/> (момента старта процесса: файлы, созданные уже этим процессом, не
    /// трогаются). Каждый файл — отдельно: занятый или недоступный пишется в журнал с путём и не мешает
    /// удалить остальные. Исключений не бросает.
    /// </summary>
    /// <returns>Сколько файлов удалено.</returns>
    public int SweepStale(DateTime olderThanUtc, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);

        var removed = 0;
        foreach (var path in Candidates(logger))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(path) >= olderThanUtc)
                {
                    continue; // создан уже этим процессом (или его часами) — не остаток
                }

                File.Delete(path);
                removed++;
            }
            catch (IOException exception)
            {
                MediaMaintenanceLog.TempFileNotRemoved(logger, exception, path);
            }
            catch (UnauthorizedAccessException exception)
            {
                MediaMaintenanceLog.TempFileNotRemoved(logger, exception, path);
            }
        }

        return removed;
    }

    /// <summary>Файлы-кандидаты; сбой перечисления каталога пишется в журнал и не прерывает остальные.</summary>
    private List<string> Candidates(ILogger logger)
    {
        var files = new List<string>();
        Collect(files, Root, "*", logger);
        foreach (var pattern in RootFilePatterns)
        {
            Collect(files, LegacyRoot, pattern, logger);
        }

        return files;
    }

    private static void Collect(List<string> files, string directory, string pattern, ILogger logger)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        try
        {
            files.AddRange(Directory.EnumerateFiles(directory, pattern, SearchOption.TopDirectoryOnly));
        }
        catch (IOException exception)
        {
            MediaMaintenanceLog.TempFolderNotListed(logger, exception, directory, pattern);
        }
        catch (UnauthorizedAccessException exception)
        {
            MediaMaintenanceLog.TempFolderNotListed(logger, exception, directory, pattern);
        }
    }
}
