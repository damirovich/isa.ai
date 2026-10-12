using System;
using System.Globalization;
using ISC.AI.Modules.Media.Application.Features.Assets;
using ISC.AI.Modules.Media.Domain.Model;
using Microsoft.Extensions.Configuration;

namespace ISC.AI.Modules.Media.Application;

/// <summary>
/// Пределы приёма файлов носителей (ТФ-МЕД-01, ADR-0040). Фото — постоянный предел
/// <see cref="MediaFileRules.MaxImageBytes"/>: изображение раскодируется в памяти целиком. Видео и аудио принимаются
/// потоком через временный файл, поэтому их предел задаёт эксплуатант по свободному месту на защищённом томе
/// (ТБ-062): файл занимает место во временном каталоге при приёме и при обработке, и ещё раз — в хранилище.
/// </summary>
/// <param name="MaxStreamedFileBytes">Предел размера видео и аудио, байт.</param>
public sealed record MediaUploadOptions(long MaxStreamedFileBytes)
{
    /// <summary>Ключ конфигурации предела размера видео и аудио, байт.</summary>
    public const string MaxStreamedFileBytesKey = "Media:Upload:MaxFileBytes";

    /// <summary>Предел видео и аудио по умолчанию — 16 ГБ: сутки записи с камеры наблюдения в обычном качестве.</summary>
    public const long DefaultMaxStreamedFileBytes = 16L * 1024 * 1024 * 1024;

    /// <summary>
    /// Верхняя граница настройки — 64 ГБ: больше не даёт приём через браузер (час и больше на один файл), а
    /// временный каталог должен выдержать несколько таких файлов одновременно.
    /// </summary>
    public const long MaxAllowedStreamedFileBytes = 64L * 1024 * 1024 * 1024;

    /// <summary>Настройки по умолчанию — для мест, где конфигурации нет (тесты, проверка формы).</summary>
    public static MediaUploadOptions Default { get; } = new(DefaultMaxStreamedFileBytes);

    /// <summary>Предел размера файла для вида носителя.</summary>
    public long MaxBytesFor(MediaKind kind) =>
        kind == MediaKind.Image ? MediaFileRules.MaxImageBytes : MaxStreamedFileBytes;

    /// <summary>
    /// Читает предел из конфигурации. Мусор и пустое значение — умолчание; значение вне допустимого — к границе:
    /// не меньше предела фото (видео меньше фото не бывает) и не больше <see cref="MaxAllowedStreamedFileBytes"/>.
    /// </summary>
    public static MediaUploadOptions Read(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var configured = long.TryParse(
            configuration[MaxStreamedFileBytesKey], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value > 0
            ? value
            : DefaultMaxStreamedFileBytes;
        return new MediaUploadOptions(Math.Clamp(configured, MediaFileRules.MaxImageBytes, MaxAllowedStreamedFileBytes));
    }
}
