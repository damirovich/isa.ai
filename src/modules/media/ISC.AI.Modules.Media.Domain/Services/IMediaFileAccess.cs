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
/// <param name="CaseRef">
/// Дело (непрозрачный идентификатор профиля) для файлов, привязанных к делу напрямую — вырезка пробы
/// (сессия поиска); для носителей и вырезок лиц <see langword="null"/>: их дело определяется по носителю
/// (<see cref="ICaseScope.IsAssetAccessibleAsync"/>).
/// </param>
public sealed record MediaFileDescriptor(
    string StoredFileName,
    string Category,
    string SubPath,
    string ContentType,
    short Classification,
    int DivisionId,
    int? CaseRef = null) : IClassified;

/// <summary>
/// Маршрут раздачи файлов пакета — ЕДИНСТВЕННЫЙ источник шаблона и для эндпоинта (Media.Data), и для
/// построения ссылок в UI (Media.UI не ссылается на Data): совпадение строк без общего источника
/// однажды разошлось бы молча (404 на всех картинках).
/// </summary>
public static class MediaFileRoutes
{
    /// <summary>Префикс маршрута.</summary>
    public const string Prefix = "/media/files";

    /// <summary>Шаблон маршрута эндпоинта; для категории <see cref="MediaFileCategories.Probes"/> второй сегмент — идентификатор сессии.</summary>
    public const string Template = Prefix + "/{category}/{assetId:int}/{storedFileName}";

    /// <summary>Ссылка на файл: <c>/media/files/{category}/{id}/{storedFileName}</c>.</summary>
    public static string Build(string category, int id, string storedFileName) =>
        Prefix + "/" + category + "/" + id.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/" + storedFileName;
}

/// <summary>Категории файлового хранилища пакета «Медиа» (подкаталоги <c>IFileStorage</c>).</summary>
public static class MediaFileCategories
{
    /// <summary>Исходные носители (фото/видео) как загружены.</summary>
    public const string Originals = "media-originals";

    /// <summary>Вырезки лиц (JPEG) для показа в выдаче поиска.</summary>
    public const string FaceCrops = "media-faces";

    /// <summary>
    /// Вырезка лица ПРОБНОГО изображения (JPEG) для показа пары «пробное ↔ кандидат» на верификации
    /// (ТФ-ВЕР-01). Хранится под решёткой сессии (ТБ-070); вектор пробы в базу не пишется (ТБ-074),
    /// оригинал пробы — только в аудите (ТБ-072).
    /// </summary>
    public const string Probes = "media-probes";
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
