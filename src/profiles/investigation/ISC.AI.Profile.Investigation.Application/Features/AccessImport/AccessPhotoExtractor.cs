using System.Buffers.Binary;

namespace ISC.AI.Profile.Investigation.Application.Features.AccessImport;

/// <summary>Снимок, извлечённый из поля базы Access.</summary>
/// <param name="Content">Байты файла изображения.</param>
/// <param name="ContentType">MIME-тип по сигнатуре.</param>
/// <param name="Extension">Расширение с точкой.</param>
public sealed record AccessPhoto(byte[] Content, string ContentType, string Extension);

/// <summary>
/// Извлечение снимка из поля «объект OLE» базы Access (ADR-0043 п. 3). Access хранит вставленную картинку внутри
/// обёртки OLE (заголовок «Package», «Paint.Picture» и т. п.), поэтому файл ищется по сигнатуре внутри поля:
/// JPEG, PNG, BMP — форматы, которые принимают материалы дела (ТС-010). Поле с самим файлом (без обёртки) тоже
/// подходит — сигнатура с нулевого байта.
/// </summary>
/// <remarks>
/// Границы файла берутся из его же структуры: конец JPEG — последний маркер EOI, PNG — блок IEND, BMP — длина из
/// заголовка. Хвост обёртки OLE за картинкой в материалы не попадает. Картинка без заголовка файла (DIB «Picture
/// (Device Independent Bitmap)», метафайлы) не распознаётся — такая запись попадает в отчёт, снимок переносится
/// вручную.
/// </remarks>
public static class AccessPhotoExtractor
{
    /// <summary>Отказ для отчёта: в поле нет снимка распознаваемого формата.</summary>
    public const string UnrecognizedMessage =
        "фото в базе не распознано (внутри поля OLE нет JPEG, PNG или BMP) — перенесите снимок вручную";

    private static ReadOnlySpan<byte> JpegStart => [0xFF, 0xD8, 0xFF];

    private static ReadOnlySpan<byte> PngStart => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private static ReadOnlySpan<byte> PngEnd => [0x49, 0x45, 0x4E, 0x44, 0xAE, 0x42, 0x60, 0x82];

    /// <summary>Снимок из содержимого поля или <see langword="null"/>, если распознать нечего.</summary>
    public static AccessPhoto? TryExtract(ReadOnlySpan<byte> blob)
    {
        // Ищется самая ранняя сигнатура: обёртка OLE стоит перед картинкой, а внутри картинки (миниатюра EXIF) может
        // встретиться вторая сигнатура — она дальше и не мешает.
        AccessPhoto? best = null;
        var bestOffset = int.MaxValue;
        Consider(TryJpeg(blob), ref best, ref bestOffset);
        Consider(TryPng(blob), ref best, ref bestOffset);
        Consider(TryBmp(blob), ref best, ref bestOffset);
        return best;
    }

    private static void Consider((int Offset, AccessPhoto Photo)? found, ref AccessPhoto? best, ref int bestOffset)
    {
        if (found is { } value && value.Offset < bestOffset)
        {
            best = value.Photo;
            bestOffset = value.Offset;
        }
    }

    private static (int, AccessPhoto)? TryJpeg(ReadOnlySpan<byte> blob)
    {
        var start = blob.IndexOf(JpegStart);
        if (start < 0)
        {
            return null;
        }

        // Конец — последний EOI (FF D9) после начала: обёртка OLE за картинкой отрезается.
        var image = blob[start..];
        var end = image.LastIndexOf((ReadOnlySpan<byte>)[0xFF, 0xD9]);
        return end < 2 ? null : (start, new AccessPhoto(image[..(end + 2)].ToArray(), "image/jpeg", ".jpg"));
    }

    private static (int, AccessPhoto)? TryPng(ReadOnlySpan<byte> blob)
    {
        var start = blob.IndexOf(PngStart);
        if (start < 0)
        {
            return null;
        }

        var image = blob[start..];
        var end = image.IndexOf(PngEnd);
        return end < 0 ? null : (start, new AccessPhoto(image[..(end + PngEnd.Length)].ToArray(), "image/png", ".png"));
    }

    private static (int, AccessPhoto)? TryBmp(ReadOnlySpan<byte> blob)
    {
        // «BM» встречается в произвольных данных часто, поэтому заголовок проверяется целиком: длина файла в пределах
        // поля, смещение растра за заголовком, размер заголовка DIB из известных (12, 40, 52, 56, 108, 124).
        for (var start = blob.IndexOf("BM"u8); start >= 0;)
        {
            var image = blob[start..];
            if (image.Length >= 26)
            {
                var size = BinaryPrimitives.ReadUInt32LittleEndian(image[2..]);
                var offset = BinaryPrimitives.ReadUInt32LittleEndian(image[10..]);
                var header = BinaryPrimitives.ReadUInt32LittleEndian(image[14..]);
                if (size <= image.Length && size > offset && offset >= 14 + header
                    && header is 12 or 40 or 52 or 56 or 108 or 124)
                {
                    return (start, new AccessPhoto(image[..(int)size].ToArray(), "image/bmp", ".bmp"));
                }
            }

            var next = blob[(start + 2)..].IndexOf("BM"u8);
            start = next < 0 ? -1 : start + 2 + next;
        }

        return null;
    }
}
