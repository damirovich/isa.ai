using ISC.AI.Abstractions.Security;

namespace ISC.AI.Modules.Media.Domain.Services;

/// <summary>
/// Описание файла носителя/вырезки для раздачи по HTTP (ТС-010, ТБ-073): режимные поля идут вместе
/// с именем, чтобы эндпоинт проверил допуск ДО открытия потока.
/// </summary>
/// <param name="StoredFileName">Имя файла в хранилище.</param>
/// <param name="Category">Категория хранилища (<see cref="MediaFileCategories"/>).</param>
/// <param name="SubPath">Подкаталог (идентификатор носителя).</param>
/// <param name="ContentType">MIME-тип для ответа.</param>
/// <param name="Classification">Гриф носителя-владельца.</param>
/// <param name="DivisionId">Подразделение носителя-владельца.</param>
public sealed record MediaFileDescriptor(
    string StoredFileName,
    string Category,
    string SubPath,
    string ContentType,
    short Classification,
    int DivisionId) : IClassified;

/// <summary>Категории файлового хранилища пакета «Медиа» (подкаталоги <c>IFileStorage</c>).</summary>
public static class MediaFileCategories
{
    /// <summary>Исходные носители (фото/видео) как загружены.</summary>
    public const string Originals = "media-originals";

    /// <summary>Вырезки лиц (JPEG) для показа в выдаче поиска.</summary>
    public const string FaceCrops = "media-faces";
}

/// <summary>
/// Разрешение имени файла в описание с режимными полями (ТБ-073). Возвращает <see langword="null"/>,
/// если файла нет ИЛИ он принадлежит другому носителю, чем указан в маршруте — наружу единый «не найден».
/// </summary>
public interface IMediaFileAccess
{
    /// <summary>Файл категории <paramref name="category"/> носителя <paramref name="assetId"/>.</summary>
    Task<MediaFileDescriptor?> ResolveAsync(
        string category, int assetId, string storedFileName, CancellationToken cancellationToken = default);
}
