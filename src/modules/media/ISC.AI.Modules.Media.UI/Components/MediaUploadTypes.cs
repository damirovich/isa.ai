using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ISC.AI.Modules.Media.Application.Features.Assets;

namespace ISC.AI.Modules.Media.UI;

/// <summary>
/// Форматы для выбора файлов в медиатеке дела (ТФ-МЕД-01; аудио — ADR-0026): фильтр диалога выбора
/// (<c>accept</c>) и MIME-тип, с которым файл уходит в команду загрузки.
/// </summary>
/// <remarks>
/// <para>
/// ИСТОЧНИК ПРАВДЫ о допустимых форматах — <see cref="MediaFileRules"/> слоя приложения: аудио в фильтр
/// попадает её MIME-типами, а не дублирующим списком. Расширения добавлены к ним потому, что диалог выбора
/// Windows строит фильтр по расширениям, а сопоставление «MIME → расширение» у браузера неполное
/// (<c>audio/amr</c>, <c>audio/3gpp</c> им часто не известны) — без расширений голосовые AMR/3GP в
/// диалоге просто не видны.
/// </para>
/// <para>
/// ТИП ФАЙЛА. Браузер сообщает тип по расширению и реестру ОС. Если этот тип из allowlist
/// <see cref="MediaFileRules"/>, он и уходит в команду: <c>.3gp</c>, заявленный как <c>video/3gpp</c>,
/// принимается как видео, как <c>audio/3gpp</c> — как аудио. Для голосовых сообщений тип часто пуст
/// (<c>.amr</c>, <c>.3gp</c>) или не из allowlist. Тогда — и ТОЛЬКО тогда — тип берётся из фиксированной
/// таблицы по расширению: звуковые расширения — из аудио-allowlist
/// <see cref="MediaFileRules.AllowedAudioContentTypes"/>, <c>.3gp</c>/<c>.3gpp</c> — <c>video/3gpp</c> из
/// видео-allowlist (тот же вид, что при типе от Chrome/Edge: вид носителя не должен зависеть от браузера).
/// Голосовой 3GP без картинки после загрузки переводит в «аудио» индексатор: видеопотока нет — вид
/// «аудио», поиск по лицу «неприменим». Таблица не ослабляет приём: семейство файла сервер всё равно
/// устанавливает по сигнатуре байтов (<c>ContentSniffer</c>, ТС-010), а все её типы — звуковые или видео
/// и раздаются браузеру либо как медиа, либо вложением (не как разметка).
/// </para>
/// </remarks>
public static class MediaUploadTypes
{
    /// <summary>Расширения изображений (форматы, которые читает конвейер лиц; TIFF/HEIC — нет, ADR-0020).</summary>
    private static readonly string[] ImageExtensions = [".jpg", ".jpeg", ".png", ".bmp", ".webp"];

    /// <summary>Расширения видео (раскадровка ffmpeg); 3GP с телефонов — тоже видео (см. remarks класса).</summary>
    private static readonly string[] VideoExtensions = [".mp4", ".webm", ".mkv", ".mov", ".avi", ".3gp", ".3gpp"];

    /// <summary>
    /// Звуковые расширения → MIME-тип из аудио-allowlist. Применяется, только если браузер не сообщил
    /// допустимый тип (см. remarks класса).
    /// </summary>
    private static readonly Dictionary<string, string> AudioTypeByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".mp3"] = "audio/mpeg",
        [".m4a"] = "audio/mp4",
        [".aac"] = "audio/aac",
        [".ogg"] = "audio/ogg",
        [".oga"] = "audio/ogg",
        [".opus"] = "audio/ogg",
        [".wav"] = "audio/wav",
        [".flac"] = "audio/flac",
        [".amr"] = "audio/amr",
    };

    /// <summary>
    /// Видеорасширения, для которых браузер нередко не сообщает тип (3GP с телефонов), → MIME-тип из
    /// видео-allowlist. Применяется, только если браузер не сообщил допустимый тип (см. remarks класса).
    /// </summary>
    private static readonly Dictionary<string, string> VideoTypeByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".3gp"] = "video/3gpp",
        [".3gpp"] = "video/3gpp",
    };

    /// <summary>
    /// Значение атрибута <c>accept</c> для выбора файлов: расширения фото, видео и аудио плюс MIME-типы
    /// аудио из <see cref="MediaFileRules.AllowedAudioContentTypes"/>.
    /// </summary>
    public static string Accept { get; } = string.Join(
        ",",
        ImageExtensions
            .Concat(VideoExtensions)
            .Concat(AudioTypeByExtension.Keys)
            .Concat(MediaFileRules.AllowedAudioContentTypes.Order(StringComparer.Ordinal))
            .Distinct(StringComparer.OrdinalIgnoreCase));

    /// <summary>Расширения звуковых файлов для подсказки у кнопки выбора (без точки, через запятую).</summary>
    public static string AudioExtensionsHint { get; } = string.Join(
        ", ", AudioTypeByExtension.Keys.Select(extension => extension[1..]));

    /// <summary>
    /// MIME-тип для команды загрузки: тип от браузера, если он из allowlist <see cref="MediaFileRules"/>;
    /// иначе — тип по расширению из фиксированных таблиц (звук; 3GP — видео); иначе — тип от браузера как
    /// есть (сервер откажет понятным сообщением о формате).
    /// </summary>
    /// <param name="fileName">Имя файла от браузера.</param>
    /// <param name="browserContentType">Тип, заявленный браузером (может быть пустым).</param>
    public static string Resolve(string? fileName, string? browserContentType)
    {
        var declared = browserContentType ?? string.Empty;
        if (MediaFileRules.IsAllowed(declared))
        {
            return declared;
        }

        var extension = Path.GetExtension(fileName ?? string.Empty);
        if (extension.Length == 0)
        {
            return declared;
        }

        if (AudioTypeByExtension.TryGetValue(extension, out var audioType)
            && MediaFileRules.AllowedAudioContentTypes.Contains(audioType))
        {
            return audioType;
        }

        return VideoTypeByExtension.TryGetValue(extension, out var videoType)
            && MediaFileRules.AllowedVideoContentTypes.Contains(videoType)
            ? videoType
            : declared;
    }
}
