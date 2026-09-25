using System;
using ISC.AI.Modules.Media.Domain.Model;

namespace ISC.AI.Modules.Media.Application.Features.Assets;

/// <summary>
/// Определение семейства файла (изображение / видео / аудио) по сигнатуре первых байтов — НЕ со слов клиента
/// (ТС-010, контракт <see cref="MediaAssetDraft.ContentType"/>). Заявленный MIME-тип сверяется с семейством:
/// файл, объявленный изображением, но являющийся видео (и наоборот), в конвейер не попадает и с чужим
/// типом раздаваться не будет. Точный формат внутри семейства окончательно устанавливает декодер конвейера.
/// </summary>
/// <remarks>
/// <para>
/// Сигнатуры: JPEG <c>FF D8 FF</c>; PNG <c>89 50 4E 47 0D 0A 1A 0A</c>; BMP <c>42 4D</c>; GIF <c>GIF8</c>;
/// WebP <c>RIFF....WEBP</c>; MP4/MOV/M4A/3GP — <c>ftyp</c> по смещению 4; Matroska/WebM <c>1A 45 DF A3</c>;
/// AVI <c>RIFF....AVI </c>. Аудио (ADR-0026): WAV <c>RIFF....WAVE</c>; Ogg (Vorbis/Opus) <c>OggS</c>;
/// FLAC <c>fLaC</c>; AMR <c>#!AMR</c>; MP3 — тег <c>ID3</c> либо заголовок кадра MPEG; AAC — ADTS
/// (<c>FFF</c> с нулевым слоем) или <c>ADIF</c>. Неизвестная сигнатура — <see langword="null"/> (приём отклоняется).
/// </para>
/// <para>
/// УНИВЕРСАЛЬНЫЕ КОНТЕЙНЕРЫ. ISO BMFF (<c>ftyp</c>) и Matroska/WebM несут и видео, и один только звук
/// (m4a, 3gp-голосовое, audio/webm): по сигнатуре контейнера это не различить. <see cref="Sniff"/> относит их
/// к видео (как и прежде), а <see cref="IsCompatible"/> допускает для них и заявленное «аудио» — дальше
/// решает ffmpeg конвейера. Подмены «изображение ↔ видео/аудио» это не открывает.
/// </para>
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

        if (IsAudioVideoContainer(content))
        {
            return MediaKind.Video; // Matroska / WebM, ISO BMFF: MP4 / MOV (звук внутри — см. IsCompatible)
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

            if (form.SequenceEqual("WAVE"u8))
            {
                return MediaKind.Audio; // WAV
            }

            return null;
        }

        return IsAudioSignature(content) ? MediaKind.Audio : null;
    }

    /// <summary>
    /// Совместимо ли содержимое с заявленным видом носителя: сигнатура распознана и её семейство совпадает с
    /// заявленным — либо заявлено аудио, а содержимое — универсальный контейнер (m4a, 3gp, webm), который
    /// по сигнатуре не отличить от видео.
    /// </summary>
    public static bool IsCompatible(ReadOnlySpan<byte> content, MediaKind declared)
    {
        var sniffed = Sniff(content);
        if (sniffed is null)
        {
            return false;
        }

        return sniffed == declared || (declared == MediaKind.Audio && IsAudioVideoContainer(content));
    }

    /// <summary>Matroska/WebM либо ISO BMFF (<c>ftyp</c>): контейнер, несущий видео или только звук.</summary>
    private static bool IsAudioVideoContainer(ReadOnlySpan<byte> content) =>
        StartsWith(content, [0x1A, 0x45, 0xDF, 0xA3])
        || (content.Length >= 8 && content.Slice(4, 4).SequenceEqual("ftyp"u8));

    /// <summary>Сигнатуры чисто звуковых форматов (кроме WAV — он разбирается вместе с прочими RIFF).</summary>
    private static bool IsAudioSignature(ReadOnlySpan<byte> content)
    {
        if (StartsWith(content, "OggS"u8) || StartsWith(content, "fLaC"u8) || StartsWith(content, "#!AMR"u8)
            || StartsWith(content, "ID3"u8) || StartsWith(content, "ADIF"u8))
        {
            return true; // Ogg (Vorbis/Opus), FLAC, AMR-NB/WB, MP3 с тегом ID3v2, AAC ADIF
        }

        if (content.Length < 3 || content[0] != 0xFF)
        {
            return false;
        }

        var b1 = content[1];

        // AAC ADTS: синхрослово 0xFFF, слой «00» (MPEG-2/4 AAC всегда пишет нулевой слой).
        if ((b1 & 0xF6) == 0xF0)
        {
            return true;
        }

        // Кадр MPEG-аудио (MP3/MP2): 11 бит синхронизации, версия не «зарезервирована» (01), слой не «00»,
        // индекс битрейта не «1111», частота не «11» — строже одной синхронизации, чтобы случайные байты
        // FF Ex не выдавались за звук.
        var b2 = content[2];
        return (b1 & 0xE0) == 0xE0
            && (b1 & 0x18) != 0x08
            && (b1 & 0x06) != 0
            && (b2 & 0xF0) != 0xF0
            && (b2 & 0x0C) != 0x0C;
    }

    private static bool StartsWith(ReadOnlySpan<byte> content, ReadOnlySpan<byte> signature) =>
        content.Length >= signature.Length && content[..signature.Length].SequenceEqual(signature);
}
