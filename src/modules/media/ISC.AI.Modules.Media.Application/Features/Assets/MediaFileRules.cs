using System;
using System.Collections.Generic;
using ISC.AI.Modules.Media.Domain.Model;

namespace ISC.AI.Modules.Media.Application.Features.Assets;

/// <summary>Правила приёма файлов носителей (ТС-010, ТФ-МЕД-01): allowlist форматов и предел размера.</summary>
/// <remarks>
/// TIFF и HEIC в allowlist НЕ входят: декодер конвейера (Skia) их не читает — носитель гарантированно уходил бы
/// в «ошибка обработки». Возврат форматов — только вместе с декодером (ADR-0020).
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
    };

    /// <summary>Формат допустим к приёму.</summary>
    public static bool IsAllowed(string? contentType) =>
        contentType is not null
        && (AllowedImageContentTypes.Contains(contentType) || AllowedVideoContentTypes.Contains(contentType));

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

        return AllowedVideoContentTypes.Contains(contentType) ? MediaKind.Video : null;
    }
}
