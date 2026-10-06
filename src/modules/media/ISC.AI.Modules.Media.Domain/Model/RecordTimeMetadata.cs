using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace ISC.AI.Modules.Media.Domain.Model;

/// <summary>
/// «Встроенное» время начала записи видео из метаданных контейнера (ТФ-МЕД-11, ADR-0038): по тегам, которые
/// отдаёт ffprobe при пробе видеопотока. Рядом с таймкодом покадрового просмотра показывается «время начала записи +
/// смещение» — то, что записала камера, а не то, что ввёл оператор.
/// </summary>
/// <remarks>
/// <para>
/// ОТКУДА БЕРЁТСЯ (первое правдоподобное значение по порядку):
/// <list type="number">
/// <item><c>com.apple.quicktime.creationdate</c> — телефоны Apple пишут МЕСТНОЕ время со смещением пояса
/// («2022-12-08T14:08:12+0600»): точнее, чем <c>creation_time</c>;</item>
/// <item><c>creation_time</c> контейнера — MP4/MOV/3GP (UTC, «…Z»), Matroska (DateUTC), AVI (IDIT — местное время
/// камеры без пояса: считается временем в поясе сервера, как EXIF без смещения у фото);</item>
/// <item><c>creation_time</c> видеопотока — если контейнер его не записал.</item>
/// </list>
/// Только дата без времени («2003-03-10», тег ICRD) — не время начала записи и не берётся.
/// </para>
/// <para>
/// Метаданные — НЕ доказательство (ТЭ-006): их сбивают часы камеры, перекодирование (тогда это время конвертации)
/// и подделка. Поэтому неправдоподобные значения (раньше 1990 года — нулевые эпохи 1904/1970 «пустых» полей —
/// или позже завтрашнего дня) отбрасываются, а показ подписывается «по метаданным файла».
/// </para>
/// </remarks>
public static partial class RecordTimeMetadata
{
    /// <summary>Раньше этого времени значение считается пустым полем, а не временем записи (как у даты съёмки фото).</summary>
    public static readonly DateTime EarliestPlausibleUtc = new(1990, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private const string AppleCreationDateKey = "com.apple.quicktime.creationdate";
    private const string CreationTimeKey = "creation_time";

    /// <summary>
    /// Время начала записи по тегам контейнера и видеопотока или <see langword="null"/>, если его нет или оно
    /// неправдоподобно.
    /// </summary>
    /// <param name="formatTags">Теги контейнера (ffprobe <c>format.tags</c>).</param>
    /// <param name="streamTags">Теги видеопотока (ffprobe <c>streams[].tags</c>).</param>
    /// <param name="localZone">Пояс для значений без смещения (AVI IDIT).</param>
    /// <param name="nowUtc">Текущее время — для отсечения дат из будущего.</param>
    public static DateTimeOffset? TryParse(
        IReadOnlyDictionary<string, string>? formatTags,
        IReadOnlyDictionary<string, string>? streamTags,
        TimeZoneInfo localZone,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(localZone);

        return Plausible(Find(formatTags, AppleCreationDateKey), localZone, nowUtc)
            ?? Plausible(Find(formatTags, CreationTimeKey), localZone, nowUtc)
            ?? Plausible(Find(streamTags, CreationTimeKey), localZone, nowUtc);
    }

    /// <summary>Значение правдоподобно как время записи: не раньше 1990 года и не позже завтрашнего дня.</summary>
    public static bool IsPlausible(DateTimeOffset value, DateTimeOffset nowUtc) =>
        value.UtcDateTime >= EarliestPlausibleUtc && value <= nowUtc.AddDays(1);

    private static DateTimeOffset? Plausible(string? raw, TimeZoneInfo localZone, DateTimeOffset nowUtc) =>
        Parse(raw, localZone) is { } value && IsPlausible(value, nowUtc) ? value : null;

    // Ключи тегов у ffprobe — как в файле; регистр у разных muxer'ов разный («CREATION_TIME» у части Matroska).
    private static string? Find(IReadOnlyDictionary<string, string>? tags, string key)
    {
        if (tags is null)
        {
            return null;
        }

        foreach (var (name, value) in tags)
        {
            if (string.Equals(name, key, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return null;
    }

    /// <summary>
    /// ISO-подобная строка ffprobe → момент: «…Z» и «±hh:mm»/«±hhmm» — как записаны; без пояса — время в
    /// <paramref name="localZone"/>. Без времени суток (только дата) — <see langword="null"/>.
    /// </summary>
    private static DateTimeOffset? Parse(string? raw, TimeZoneInfo localZone)
    {
        if (raw is null || !raw.Contains(':', StringComparison.Ordinal))
        {
            return null;
        }

        // «+0600» → «+06:00»: так смещение разбирает DateTimeOffset.
        var value = CompactOffset().Replace(raw, "$1:$2");
        if (ExplicitZone().IsMatch(value))
        {
            return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var zoned) ? zoned : null;
        }

        if (!DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
        {
            return null;
        }

        var unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        return new DateTimeOffset(unspecified, localZone.GetUtcOffset(unspecified));
    }

    [GeneratedRegex(@"([+-]\d{2})(\d{2})$", RegexOptions.CultureInvariant)]
    private static partial Regex CompactOffset();

    [GeneratedRegex(@"(Z|[+-]\d{2}:\d{2})$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex ExplicitZone();
}
