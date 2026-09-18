using ISC.AI.Modules.Media.Domain.Services;

namespace ISC.AI.Modules.Media.UI;

/// <summary>
/// Построитель защищённых ссылок раздачи файлов пакета «Медиа» (<c>GET /media/files/{category}/{id}/{name}</c>,
/// см. <c>MediaFileEndpoints</c>). Допуск проверяет СЕРВЕР при каждой отдаче (ТБ-020/021, ТБ-073):
/// недоступный файл отдаётся единым 404 — на странице это «битая картинка», а не подсказка о существовании.
/// Категории — из <see cref="MediaFileCategories"/>, шаблон маршрута — из <see cref="MediaFileRoutes"/>
/// (общий источник с эндпоинтом слоя данных): литерал здесь не дублируется, чтобы перенос эндпоинта
/// не давал молчаливых 404 на всех картинках.
/// </summary>
public static class MediaFileUrl
{
    /// <summary>Исходник носителя (фото/видео) как загружен — без «улучшений» (ТЭ-007).</summary>
    public static string Originals(int assetId, string storedFileName) =>
        MediaFileRoutes.Build(MediaFileCategories.Originals, assetId, storedFileName);

    /// <summary>Вырезка лица носителя (JPEG): подкаталог — носитель, которому принадлежит лицо.</summary>
    public static string FaceCrop(int assetId, string storedFileName) =>
        MediaFileRoutes.Build(MediaFileCategories.FaceCrops, assetId, storedFileName);

    /// <summary>Вырезка пробы поисковой сессии: сегмент «носитель» маршрута — идентификатор СЕССИИ.</summary>
    public static string ProbeCrop(int sessionId, string storedFileName) =>
        MediaFileRoutes.Build(MediaFileCategories.Probes, sessionId, storedFileName);
}
