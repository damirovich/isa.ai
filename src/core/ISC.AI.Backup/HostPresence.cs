namespace ISC.AI.Backup;

/// <summary>
/// Отметка «система запущена» (ТБ-082, ADR-0063): хост всё время работы держит файл <c>_restore-journal/host.lock</c> в
/// хранилище ядра открытым без совместного доступа; программа восстановления пробует его открыть и при занятости
/// отказывает.
/// </summary>
/// <remarks>
/// ИНВАРИАНТ: восстановление «на живую» не проходит, даже когда простаивающий хост закрыл все соединения с базой (пул
/// Npgsql закрывает их через несколько минут простоя, и проверка сеансов одна этого не видит). Каталог отметок в копию не
/// входит (<see cref="StorageArchive.RestoreJournalFolder"/>), поэтому открытый файл копии не мешает.
/// </remarks>
public static class HostPresence
{
    /// <summary>Имя файла отметки в каталоге отметок хранилища ядра.</summary>
    public const string FileName = "host.lock";

    /// <summary>
    /// Занимает отметку на время работы хоста (освобождается при закрытии потока или завершении процесса); занята другим
    /// процессом — <see langword="null"/>.
    /// </summary>
    public static FileStream? TryHold(string storageRoot)
    {
        var folder = Path.Combine(storageRoot, StorageArchive.RestoreJournalFolder);
        Directory.CreateDirectory(folder);
        try
        {
            return new FileStream(Path.Combine(folder, FileName), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>Отметка занята — система запущена на этом хранилище.</summary>
    public static bool IsHeld(string storageRoot)
    {
        var path = Path.Combine(storageRoot, StorageArchive.RestoreJournalFolder, FileName);
        try
        {
            using var probe = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
        catch (IOException)
        {
            return true;
        }
    }
}
