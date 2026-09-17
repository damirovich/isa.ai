using System.Globalization;
using ISC.AI.Modules.Media.Domain.Services;

namespace ISC.AI.Modules.Media.UI;

/// <summary>
/// Построитель защищённых ссылок раздачи файлов пакета «Медиа» (<c>GET /media/files/{category}/{id}/{name}</c>,
/// см. <c>MediaFileEndpoints</c>). Допуск проверяет СЕРВЕР при каждой отдаче (ТБ-020/021, ТБ-073):
/// недоступный файл отдаётся единым 404 — на странице это «битая картинка», а не подсказка о существовании.
/// Категории — из <see cref="MediaFileCategories"/>, чтобы маршрут и хранилище не разошлись.
/// </summary>
public static class MediaFileUrl
{
    /// <summary>Исходник носителя (фото/видео) как загружен — без «улучшений» (ТЭ-007).</summary>
    public static string Originals(int assetId, string storedFileName) =>
        Build(MediaFileCategories.Originals, assetId, storedFileName);

    /// <summary>Вырезка лица носителя (JPEG): подкаталог — носитель, которому принадлежит лицо.</summary>
    public static string FaceCrop(int assetId, string storedFileName) =>
        Build(MediaFileCategories.FaceCrops, assetId, storedFileName);

    /// <summary>Вырезка пробы поисковой сессии: сегмент «носитель» маршрута — идентификатор СЕССИИ.</summary>
    public static string ProbeCrop(int sessionId, string storedFileName) =>
        Build(MediaFileCategories.Probes, sessionId, storedFileName);

    private static string Build(string category, int id, string storedFileName) =>
        $"/media/files/{category}/{id.ToString(CultureInfo.InvariantCulture)}/{storedFileName}";
}
