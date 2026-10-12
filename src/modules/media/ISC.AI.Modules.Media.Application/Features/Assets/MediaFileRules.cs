using System;
using System.Collections.Generic;
using System.Globalization;
using ISC.AI.Modules.Media.Domain.Model;

namespace ISC.AI.Modules.Media.Application.Features.Assets;

/// <summary>
/// Правила приёма файлов носителей (ТС-010, ТФ-МЕД-01; аудио — ADR-0026, предлагаемый ТФ-МЕД-07): allowlist
/// форматов и предел размера фото. Предел видео и аудио настраивается — <see cref="MediaUploadOptions"/> (ADR-0040).
/// </summary>
/// <remarks>
/// TIFF и HEIC в allowlist НЕ входят: декодер конвейера (Skia) их не читает — носитель гарантированно уходил бы
/// в «ошибка обработки». Возврат форматов — только вместе с декодером (ADR-0020). Аудио читает ffmpeg
/// конвейера расшифровки (ADR-0026) — список ограничен форматами, которые реально приходят в дела:
/// голосовые сообщения мессенджеров (ogg/opus, m4a, amr, 3gp), диктофон (mp3, m4a, wav), записи разговоров.
/// </remarks>
public static class MediaFileRules
{
    /// <summary>
    /// Максимальный размер фото — 200 МБ. Файл принимается потоком (ADR-0040), но индексатор раскодирует
    /// изображение в памяти целиком: предел защищает память сервера. Видео и аудио — до
    /// <see cref="MediaUploadOptions.MaxStreamedFileBytes"/>.
    /// </summary>
    public const long MaxImageBytes = 200L * 1024 * 1024;

    /// <summary>Максимальная длина исходного имени файла (для отображения; на диске — GUID).</summary>
    public const int MaxFileNameLength = 260;

    /// <summary>Допустимые MIME-типы изображений (только те, что декодирует конвейер).</summary>
    public static readonly IReadOnlySet<string> AllowedImageContentTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "image/jpeg", "image/png", "image/bmp", "image/webp",
    };

    /// <summary>Допустимые MIME-типы видео (раскадровка — ffmpeg, ТО-мат-06).</summary>
    public static readonly IReadOnlySet<string> AllowedVideoContentTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "video/mp4", "video/webm", "video/x-matroska", "video/quicktime", "video/x-msvideo",
        // 3GP с телефонов (ADR-0026): браузеры объявляют .3gp как video/3gpp, даже когда внутри только
        // голос (без типа от браузера интерфейс по расширению ставит тот же video/3gpp). Принимаем как видео:
        // звук расшифровывается в любом случае; есть картинка — раскадровка и поиск по лицу. Нет видеопотока —
        // индексация лиц видит это пробой потоков ДО раскадровки и переводит носитель в аудиозаписи с «поиск по
        // лицу неприменим»: без «ошибки обработки» и без записи в журнал об индексации биометрии, которой не
        // было (MediaIndexer). Как аудио такой файл терял бы поиск по лицу, если это всё-таки видео.
        "video/3gpp",
    };

    /// <summary>
    /// Допустимые MIME-типы аудио (ADR-0026): звук приводится ffmpeg к формату модели распознавания. Синонимы
    /// (<c>audio/x-m4a</c>, <c>audio/x-wav</c>) — потому что браузеры заявляют один и тот же файл по-разному.
    /// </summary>
    public static readonly IReadOnlySet<string> AllowedAudioContentTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "audio/mpeg", "audio/mp4", "audio/x-m4a", "audio/aac", "audio/ogg", "audio/opus",
        "audio/wav", "audio/x-wav", "audio/webm", "audio/flac", "audio/amr", "audio/3gpp",
    };

    /// <summary>Размер для сообщений человеку: «200 МБ», «16 ГБ», «1,5 ГБ».</summary>
    public static string DescribeSize(long bytes)
    {
        const double Megabyte = 1024d * 1024;
        const double Gigabyte = Megabyte * 1024;
        var culture = CultureInfo.GetCultureInfo("ru-RU");
        return bytes >= Gigabyte
            ? (bytes / Gigabyte).ToString("0.#", culture) + " ГБ"
            : (bytes / Megabyte).ToString("0.#", culture) + " МБ";
    }

    /// <summary>Формат допустим к приёму.</summary>
    public static bool IsAllowed(string? contentType) => KindOf(contentType) is not null;

    /// <summary>Вид носителя по MIME-типу; <see langword="null"/> — формат не из allowlist.</summary>
    public static MediaKind? KindOf(string? contentType)
    {
        if (contentType is null)
        {
            return null;
        }

        if (AllowedImageContentTypes.Contains(contentType))
        {
            return MediaKind.Image;
        }

        if (AllowedVideoContentTypes.Contains(contentType))
        {
            return MediaKind.Video;
        }

        return AllowedAudioContentTypes.Contains(contentType) ? MediaKind.Audio : null;
    }
}
