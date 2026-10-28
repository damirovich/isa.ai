using System.Security.Cryptography;

namespace ISC.AI.Backup;

/// <summary>
/// Поток только на запись: передаёт данные дальше и попутно считает SHA-256 и объём — для манифеста копии без повторного
/// чтения многогигабайтного файла. Ход записи сообщает обратным вызовом (байты с начала потока).
/// </summary>
internal sealed class HashingWriteStream(Stream inner, Action<long>? progress = null) : Stream
{
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

    /// <summary>Записано байт.</summary>
    public long Written { get; private set; }

    /// <summary>SHA-256 записанного (вызывать после окончания записи).</summary>
    public string HashHex() => Convert.ToHexString(_hash.GetCurrentHash());

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => Written;

    public override long Position
    {
        get => Written;
        set => throw new NotSupportedException();
    }

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        inner.Write(buffer);
        Account(buffer);
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        Account(buffer.Span);
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Flush() => inner.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _hash.Dispose();
        }

        base.Dispose(disposing);
    }

    private void Account(ReadOnlySpan<byte> buffer)
    {
        _hash.AppendData(buffer);
        Written += buffer.Length;
        progress?.Invoke(Written);
    }
}
