using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace ISC.AI.Modules.Media.Application.Features.Assets;

/// <summary>
/// Поток приёма носителя (ADR-0040): сначала отдаёт уже прочитанный заголовок (по нему проверялась сигнатура,
/// ТС-010), затем — остаток исходного потока, и считает прочитанное. Как только объём превысит предел, чтение
/// прерывается <see cref="MediaUploadTooLargeException"/>: заявленной длине не доверяют, а без счётчика лживый
/// источник заполнил бы временный каталог (ТБ-062).
/// </summary>
internal sealed class UploadContentStream : Stream
{
    private readonly byte[] _header;
    private readonly int _headerLength;
    private readonly Stream _inner;
    private readonly long _maxBytes;
    private int _headerPosition;
    private long _total;

    /// <param name="header">Буфер с уже прочитанным началом файла.</param>
    /// <param name="headerLength">Сколько байтов буфера занято.</param>
    /// <param name="inner">Остаток исходного потока (закрывается вместе с этим).</param>
    /// <param name="maxBytes">Предел общего объёма, байт.</param>
    public UploadContentStream(byte[] header, int headerLength, Stream inner, long maxBytes)
    {
        ArgumentNullException.ThrowIfNull(header);
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentOutOfRangeException.ThrowIfNegative(headerLength);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(headerLength, header.Length);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);

        _header = header;
        _headerLength = headerLength;
        _inner = inner;
        _maxBytes = maxBytes;
    }

    /// <inheritdoc />
    public override bool CanRead => true;

    /// <inheritdoc />
    public override bool CanSeek => false;

    /// <inheritdoc />
    public override bool CanWrite => false;

    /// <inheritdoc />
    public override long Length => throw new NotSupportedException();

    /// <summary>Сколько байтов уже отдано читателю.</summary>
    public override long Position
    {
        get => _total;
        set => throw new NotSupportedException();
    }

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    /// <inheritdoc />
    public override int Read(Span<byte> buffer)
    {
        var read = TakeFromHeader(buffer);
        if (read == 0)
        {
            read = _inner.Read(buffer);
        }

        return Count(read);
    }

    /// <inheritdoc />
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    /// <inheritdoc />
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var read = TakeFromHeader(buffer.Span);
        if (read == 0)
        {
            read = await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        }

        return Count(read);
    }

    /// <inheritdoc />
    public override void Flush()
    {
    }

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner.Dispose();
        }

        base.Dispose(disposing);
    }

    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        await _inner.DisposeAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }

    private int TakeFromHeader(Span<byte> buffer)
    {
        var left = _headerLength - _headerPosition;
        if (left <= 0 || buffer.IsEmpty)
        {
            return 0;
        }

        var take = Math.Min(left, buffer.Length);
        _header.AsSpan(_headerPosition, take).CopyTo(buffer);
        _headerPosition += take;
        return take;
    }

    private int Count(int read)
    {
        _total += read;
        if (_total > _maxBytes)
        {
            throw new MediaUploadTooLargeException(_maxBytes);
        }

        return read;
    }
}

/// <summary>Файл оказался больше предела приёма — по фактически прочитанному объёму, а не по заявленному (ADR-0040).</summary>
public sealed class MediaUploadTooLargeException : IOException
{
    /// <summary>Создаёт исключение для предела <paramref name="maxBytes"/>.</summary>
    public MediaUploadTooLargeException(long maxBytes)
        : base($"Файл больше допустимых {MediaFileRules.DescribeSize(maxBytes)}.")
    {
        MaxBytes = maxBytes;
    }

    /// <summary>Создаёт исключение без предела (для сериализации и анализаторов).</summary>
    public MediaUploadTooLargeException()
    {
    }

    /// <summary>Создаёт исключение с сообщением.</summary>
    public MediaUploadTooLargeException(string message)
        : base(message)
    {
    }

    /// <summary>Создаёт исключение с сообщением и причиной.</summary>
    public MediaUploadTooLargeException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Предел приёма, байт.</summary>
    public long MaxBytes { get; }
}
