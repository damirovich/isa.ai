using System;
using System.IO;
using System.Threading;

namespace ISC.AI.Modules.Media.Application.Features.Assets;

/// <summary>
/// Содержимое загружаемого носителя: поток и заявленная длина (ТФ-МЕД-01, ADR-0040). Файл не держится в памяти
/// целиком: обработчик читает поток один раз и сразу пишет во временный файл хранилища.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Length"/> — то, что заявил источник (браузер, каталог импорта); ему не доверяют: фактический объём
/// считается при чтении, и превышение предела прерывает приём (<see cref="UploadContentStream"/>).
/// </para>
/// <para>
/// Поток ОДНОРАЗОВЫЙ: обработчик забирает его (<see cref="TakeStream"/>) и закрывает сам. Маленькие файлы (фото
/// эталона, проба) удобнее передавать массивом — для этого неявное приведение из <c>byte[]</c>.
/// </para>
/// </remarks>
public sealed class MediaUploadContent
{
    private Stream? _stream;

    private MediaUploadContent(Stream stream, long length)
    {
        _stream = stream;
        Length = length;
    }

    /// <summary>Заявленная длина содержимого, байт.</summary>
    public long Length { get; }

    /// <summary>Содержимое из потока (не обязательно перематываемого) с заявленной длиной.</summary>
    public static MediaUploadContent FromStream(Stream stream, long length)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        return new MediaUploadContent(stream, length);
    }

    /// <summary>Содержимое из массива байт.</summary>
    public static MediaUploadContent FromByteArray(byte[] content)
    {
        ArgumentNullException.ThrowIfNull(content);
        return new MediaUploadContent(new MemoryStream(content, writable: false), content.LongLength);
    }

    /// <summary>Приведение массива байт к содержимому загрузки (<see cref="FromByteArray"/>).</summary>
    public static implicit operator MediaUploadContent(byte[] content) => FromByteArray(content);

    /// <summary>
    /// Забирает поток для чтения; закрывать его — забравшему. Повторный вызов — ошибка: второе чтение того же
    /// потока молча дало бы пустой или обрезанный файл.
    /// </summary>
    internal Stream TakeStream() =>
        Interlocked.Exchange(ref _stream, null)
        ?? throw new InvalidOperationException("Содержимое загрузки уже прочитано.");
}
