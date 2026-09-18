using System;
using ISC.AI.Modules.Media.Domain.Model;

namespace ISC.AI.Modules.Media.Application.Features.Assets;

/// <summary>
/// Определение семейства файла (изображение / видео) по сигнатуре первых байтов — НЕ со слов клиента
/// (ТС-010, контракт <see cref="MediaAssetDraft.ContentType"/>). Заявленный MIME-тип сверяется с семейством:
/// файл, объявленный изображением, но являющийся видео (и наоборот), в конвейер не попадает и с чужим
/// типом раздаваться не будет. Точный формат внутри семейства окончательно устанавливает декодер конвейера.
/// </summary>
/// <remarks>
/// Сигнатуры: JPEG <c>FF D8 FF</c>; PNG <c>89 50 4E 47 0D 0A 1A 0A</c>; BMP <c>42 4D</c>; GIF <c>GIF8</c>;
/// WebP <c>RIFF....WEBP</c>; MP4/MOV — <c>ftyp</c> по смещению 4; Matroska/WebM <c>1A 45 DF A3</c>;
/// AVI <c>RIFF....AVI </c>. Неизвестная сигнатура — <see langword="null"/> (приём отклоняется).
/// </remarks>
public static class ContentSniffer
{
    /// <summary>Минимум байтов, по которым решение принимается уверенно (RIFF-контейнеры требуют 12).</summary>
    public const int SignatureLength = 12;

    /// <summary>Семейство содержимого по сигнатуре; <see langword="null"/> — сигнатура не распознана.</summary>
    public static MediaKind? Sniff(ReadOnlySpan<byte> content)
    {
        if (StartsWith(content, [0xFF, 0xD8, 0xFF]))
        {
            return MediaKind.Image; // JPEG
        }

        if (StartsWith(content, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]))
        {
            return MediaKind.Image; // PNG
        }

        if (StartsWith(content, "BM"u8))
        {
            return MediaKind.Image; // BMP
        }

        if (StartsWith(content, "GIF8"u8))
        {
            return MediaKind.Image; // GIF
        }

        if (StartsWith(content, [0x1A, 0x45, 0xDF, 0xA3]))
        {
            return MediaKind.Video; // Matroska / WebM
        }

        if (content.Length >= 8 && content.Slice(4, 4).SequenceEqual("ftyp"u8))
        {
            return MediaKind.Video; // ISO BMFF: MP4 / MOV
        }

        if (content.Length >= SignatureLength && StartsWith(content, "RIFF"u8))
        {
            var form = content.Slice(8, 4);
            if (form.SequenceEqual("WEBP"u8))
            {
                return MediaKind.Image; // WebP
            }

            if (form.SequenceEqual("AVI "u8))
            {
                return MediaKind.Video; // AVI
            }
        }

        return null;
    }

    private static bool StartsWith(ReadOnlySpan<byte> content, ReadOnlySpan<byte> signature) =>
        content.Length >= signature.Length && content[..signature.Length].SequenceEqual(signature);
}
