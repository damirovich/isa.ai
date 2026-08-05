namespace ISC.AI.Modules.DocFlow.Domain.Services;

/// <summary>Загружаемый файл (байты + метаданные) — вход файловых операций модуля.</summary>
public sealed record UploadedFile(string FileName, string ContentType, byte[] Content);

/// <summary>Категории файлового хранилища модуля (структура каталогов — перенос из СКИД).</summary>
public static class FileCategories
{
    /// <summary>Версионируемые файлы документов (§3.3).</summary>
    public const string Documents = "documents";

    /// <summary>Сопутствующие файлы документов.</summary>
    public const string Attachments = "attachments";

    /// <summary>Файлы к переходам статусов (§4.2).</summary>
    public const string StatusHistory = "status-history";

    /// <summary>Файлы-обоснования продлений (§4.6).</summary>
    public const string DeadlineExtensions = "deadline-extensions";
}

/// <summary>
/// Порт файлового хранилища модуля (перенос <c>IFileStorageService</c> СКИД): защищённое локальное
/// хранение в контуре (air-gap), имена — случайные (исходное имя файла остаётся только в БД).
/// Просмотр файлов в интерфейсе без скачивания (§3.3) — этап 4.3.
/// </summary>
public interface IDocFlowFileStorage
{
    /// <summary>Сохраняет содержимое; возвращает сгенерированное имя в хранилище.</summary>
    Task<string> SaveAsync(
        Stream content, string extension, string category, string subPath,
        CancellationToken cancellationToken = default);

    /// <summary>Открывает файл на чтение. <see cref="FileNotFoundException"/> — файла нет.</summary>
    Task<Stream> OpenReadAsync(
        string storedFileName, string category, string subPath,
        CancellationToken cancellationToken = default);

    /// <summary>Удаляет файл (отсутствие — не ошибка: компенсация после сбоя БД должна быть идемпотентной).</summary>
    Task DeleteAsync(
        string storedFileName, string category, string subPath,
        CancellationToken cancellationToken = default);
}
