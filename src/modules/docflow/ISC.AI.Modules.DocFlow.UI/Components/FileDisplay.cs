namespace ISC.AI.Modules.DocFlow.UI;

/// <summary>Запрос на предпросмотр файла: готовая защищённая ссылка и имя для заголовка диалога.</summary>
/// <remarks>
/// Ссылку строит ТОТ, кто знает категорию и родителя (секция файлов — документ, лента назначения —
/// назначение), а диалог предпросмотра получает её готовой: ему всё равно, чей это файл.
/// </remarks>
public sealed record FilePreviewRequest(string Url, string FileName);

/// <summary>
/// Общие правила показа файлов на карточке документа: ссылки раздачи, категории, размер.
/// </summary>
/// <remarks>
/// Вынесено из <c>DocumentCard</c> при разрезе страницы на компоненты (2026-08-10): ссылку строят
/// три секции (файлы, комментарии, лента назначения), и три копии правила разошлись бы при первом
/// же изменении маршрута раздачи.
/// </remarks>
public static class FileDisplay
{
    /// <summary>
    /// Категории маршрута раздачи. Строки ТОЛЬКО отражают маршрут (<c>/docflow/files/…</c>),
    /// не завязаны на реализацию хранилища.
    /// </summary>
    public static class Categories
    {
        /// <summary>Версионируемые файлы документа (§3.3).</summary>
        public const string Documents = "documents";

        /// <summary>Сопутствующие вложения (§3.3.1).</summary>
        public const string Attachments = "attachments";

        /// <summary>Файлы комментариев (§4.8).</summary>
        public const string Comments = "comments";
    }

    /// <summary>
    /// Ссылка раздачи файла. <paramref name="parentId"/> — идентификатор ДОКУМЕНТА для файлов,
    /// вложений и комментариев, но НАЗНАЧЕНИЯ для файлов переходов и продлений
    /// (см. <c>DocumentFileAccessResolver</c>): подставить не того родителя значит получить 404.
    /// </summary>
    public static string Url(string category, int parentId, string storedFileName, bool download = false)
    {
        var url = $"/docflow/files/{category}/{parentId}/{storedFileName}";
        return download ? url + "?download=true" : url;
    }

    /// <summary>Человекочитаемый размер файла.</summary>
    public static string FormatSize(long bytes) => bytes switch
    {
        >= 1024 * 1024 => $"{bytes / (1024.0 * 1024.0):0.#} МБ",
        >= 1024 => $"{bytes / 1024.0:0.#} КБ",
        _ => $"{bytes} Б",
    };
}
