using System;
using System.Collections.Generic;
using ISC.AI.Modules.Media.Domain.Model;

namespace ISC.AI.Modules.Media.Application.Features.Assets;

/// <summary>
/// Правила приёма файлов носителей (ТС-010, ТФ-МЕД-01; аудио — ADR-0026, предлагаемый ТФ-МЕД-07): allowlist
/// форматов и предел размера.
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
    /// Максимальный размер файла носителя — 200 МБ. Содержимое команды приходит массивом байт (как у
    /// документооборота), т.е. файл целиком в памяти сервера на время приёма: предел — это и защита
    /// памяти Blazor Server, а не только дисковой квоты. Потоковый приём — отдельная задача.
    /// </summary>
    public const long MaxFileBytes = 200L * 1024 * 1024;

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
