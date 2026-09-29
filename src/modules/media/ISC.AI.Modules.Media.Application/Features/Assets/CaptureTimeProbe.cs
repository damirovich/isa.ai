using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace ISC.AI.Modules.Media.Application.Features.Assets;

/// <summary>
/// Дата и время съёмки из метаданных файла (ТФ-МЕД-17, Приложение В ТЗ, вопрос 9): значение ПО УМОЛЧАНИЮ,
/// которое оператор подтверждает или исправляет перед загрузкой. Читает только начало файла
/// (<see cref="HeadBytes"/>) — без сторонних библиотек и без обращения к сети:
/// <list type="bullet">
/// <item>JPEG — EXIF: <c>DateTimeOriginal</c> (0x9003), иначе <c>DateTimeDigitized</c> (0x9004), иначе
/// <c>DateTime</c> (0x0132); смещение пояса — из <c>OffsetTimeOriginal</c> (0x9011), если камера его записала,
/// иначе время считается местным для оператора;</item>
/// <item>MP4/MOV/3GP — <c>mvhd.creation_time</c> (секунды от 1904-01-01 UTC), если заголовок <c>moov</c> лежит в
/// начале файла (так пишут большинство камер и конвертеров; если он в конце — даты нет, вводится вручную).</item>
/// </list>
/// </summary>
/// <remarks>
/// Метаданные — не доказательство: их легко подделать или сбить часами камеры. Поэтому результат только
/// предлагается, решение за человеком, и нечитаемые, нулевые или неправдоподобные значения (до 1990 года или
/// позже завтрашнего дня) отбрасываются, а не показываются как «найденные».
/// </remarks>
public static class CaptureTimeProbe
{
    /// <summary>Сколько байт начала файла достаточно для поиска метаданных.</summary>
    public const int HeadBytes = 4 * 1024 * 1024;

    private static readonly DateTime Mp4Epoch = new(1904, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime EarliestPlausible = new(1990, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// Дата съёмки из начала файла или <see langword="null"/>, если её нет или она неправдоподобна.
    /// </summary>
    /// <param name="head">Начало файла (до <see cref="HeadBytes"/>).</param>
    /// <param name="localZone">Пояс оператора — для EXIF без смещения.</param>
    /// <param name="nowUtc">Текущее время (для отсечения дат из будущего).</param>
    public static DateTimeOffset? TryRead(ReadOnlySpan<byte> head, TimeZoneInfo localZone, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(localZone);

        DateTimeOffset? found;
        try
        {
            found = IsJpeg(head) ? ReadJpeg(head, localZone) : ReadIsoMedia(head);
        }
        catch (ArgumentOutOfRangeException)
        {
            found = null; // битые смещения в метаданных — «даты нет», а не ошибка загрузки
        }

        return found is { } value && value.UtcDateTime >= EarliestPlausible && value <= nowUtc.AddDays(1) ? value : null;
    }

    // --- JPEG / EXIF ---

    private static bool IsJpeg(ReadOnlySpan<byte> data) => data.Length > 4 && data[0] == 0xFF && data[1] == 0xD8;

    private static DateTimeOffset? ReadJpeg(ReadOnlySpan<byte> data, TimeZoneInfo localZone)
    {
        var i = 2;
        while (i + 4 <= data.Length && data[i] == 0xFF)
        {
            var marker = data[i + 1];
            if (marker is 0xD9 or 0xDA)
            {
                break; // конец изображения или начало данных — метаданных дальше нет
            }

            var length = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(i + 2, 2));
            if (length < 2 || i + 2 + length > data.Length)
            {
                break;
            }

            var segment = data.Slice(i + 4, length - 2);
            if (marker == 0xE1 && segment.Length > 14 && segment[..6].SequenceEqual("Exif\0\0"u8))
            {
                return ReadTiff(segment[6..], localZone);
            }

            i += 2 + length;
        }

        return null;
    }

    private static DateTimeOffset? ReadTiff(ReadOnlySpan<byte> tiff, TimeZoneInfo localZone)
    {
        bool little;
        if (tiff[0] == (byte)'I' && tiff[1] == (byte)'I') { little = true; }
        else if (tiff[0] == (byte)'M' && tiff[1] == (byte)'M') { little = false; }
        else { return null; }

        if (U16(tiff, 2, little) != 42)
        {
            return null;
        }

        var ifd0 = (int)U32(tiff, 4, little);
        string? dateTime = null, original = null, digitized = null, offset = null;
        var exifIfd = -1;

        foreach (var (tag, type, count, valueOffset) in Entries(tiff, ifd0, little))
        {
            if (tag == 0x0132) { dateTime = Ascii(tiff, type, count, valueOffset, little); }
            else if (tag == 0x8769) { exifIfd = (int)U32(tiff, valueOffset, little); }
        }

        if (exifIfd > 0)
        {
            foreach (var (tag, type, count, valueOffset) in Entries(tiff, exifIfd, little))
            {
                if (tag == 0x9003) { original = Ascii(tiff, type, count, valueOffset, little); }
                else if (tag == 0x9004) { digitized = Ascii(tiff, type, count, valueOffset, little); }
                else if (tag == 0x9011) { offset = Ascii(tiff, type, count, valueOffset, little); }
            }
        }

        var text = original ?? digitized ?? dateTime;
        if (text is null
            || !DateTime.TryParseExact(text.Trim(), "yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
        {
            return null;
        }

        // Смещение, записанное камерой («+06:00»), точнее пояса оператора: съёмка могла быть в другом поясе.
        var zone = offset?.Trim();
        if (zone is { Length: > 0 }
            && TimeSpan.TryParseExact(zone.TrimStart('+', '-'), @"hh\:mm", CultureInfo.InvariantCulture, out var shift))
        {
            return new DateTimeOffset(local, zone.StartsWith('-') ? -shift : shift);
        }

        return new DateTimeOffset(local, localZone.GetUtcOffset(local));
    }

    private static List<(ushort Tag, ushort Type, uint Count, int ValueOffset)> Entries(ReadOnlySpan<byte> tiff, int ifd, bool little)
    {
        var result = new List<(ushort, ushort, uint, int)>();
        if (ifd <= 0 || ifd + 2 > tiff.Length)
        {
            return result;
        }

        var count = U16(tiff, ifd, little);
        for (var n = 0; n < count; n++)
        {
            var entry = ifd + 2 + (n * 12);
            if (entry + 12 > tiff.Length)
            {
                break;
            }

            // Поле значения — последние 4 байта записи: либо само значение (до 4 байт), либо смещение к нему.
            result.Add((U16(tiff, entry, little), U16(tiff, entry + 2, little), U32(tiff, entry + 4, little), entry + 8));
        }

        return result;
    }

    private static string? Ascii(ReadOnlySpan<byte> tiff, ushort type, uint count, int valueField, bool little)
    {
        if (type != 2 || count == 0 || count > 64)
        {
            return null; // тип ASCII; даты EXIF — 20 байт, смещения — 7
        }

        var start = count <= 4 ? valueField : (int)U32(tiff, valueField, little);
        if (start < 0 || start + (int)count > tiff.Length)
        {
            return null;
        }

        return Encoding.ASCII.GetString(tiff.Slice(start, (int)count)).TrimEnd('\0');
    }

    private static ushort U16(ReadOnlySpan<byte> data, int at, bool little) =>
        little ? BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(at, 2)) : BinaryPrimitives.ReadUInt16BigEndian(data.Slice(at, 2));

    private static uint U32(ReadOnlySpan<byte> data, int at, bool little) =>
        little ? BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(at, 4)) : BinaryPrimitives.ReadUInt32BigEndian(data.Slice(at, 4));

    // --- MP4 / MOV / 3GP (ISO base media) ---

    private static DateTimeOffset? ReadIsoMedia(ReadOnlySpan<byte> data)
    {
        // Файл ISO-медиа начинается с известного ящика; иначе это не он, и «дата» из случайных байт не нужна.
        if (data.Length < 16 || !IsKnownTopBox(data.Slice(4, 4)))
        {
            return null;
        }

        var moov = FindBox(data, "moov"u8);
        if (moov.IsEmpty)
        {
            return null;
        }

        var mvhd = FindBox(moov, "mvhd"u8);
        if (mvhd.Length < 12)
        {
            return null;
        }

        var version = mvhd[0];
        ulong seconds = version == 1
            ? BinaryPrimitives.ReadUInt64BigEndian(mvhd.Slice(4, 8))
            : BinaryPrimitives.ReadUInt32BigEndian(mvhd.Slice(4, 4));
        if (seconds == 0 || seconds > 10_000_000_000UL)
        {
            return null; // «не задано» или мусор
        }

        return new DateTimeOffset(Mp4Epoch.AddSeconds(seconds));
    }

    private static bool IsKnownTopBox(ReadOnlySpan<byte> type) =>
        type.SequenceEqual("ftyp"u8) || type.SequenceEqual("moov"u8) || type.SequenceEqual("wide"u8)
        || type.SequenceEqual("mdat"u8) || type.SequenceEqual("free"u8) || type.SequenceEqual("skip"u8);

    // Содержимое первого ящика нужного типа на этом уровне (без заголовка) или пусто.
    private static ReadOnlySpan<byte> FindBox(ReadOnlySpan<byte> data, ReadOnlySpan<byte> type)
    {
        var i = 0;
        while (i + 8 <= data.Length)
        {
            long size = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(i, 4));
            var header = 8;
            if (size == 1)
            {
                if (i + 16 > data.Length) { return default; }
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(data.Slice(i + 8, 8));
                header = 16;
            }
            else if (size == 0)
            {
                size = data.Length - i; // до конца файла
            }

            if (size < header)
            {
                return default;
            }

            if (data.Slice(i + 4, 4).SequenceEqual(type))
            {
                var available = (int)Math.Min(size - header, data.Length - i - header);
                return data.Slice(i + header, available);
            }

            if (size > data.Length - i)
            {
                return default; // ящик уходит за прочитанное начало файла
            }

            i += (int)size;
        }

        return default;
    }
}
