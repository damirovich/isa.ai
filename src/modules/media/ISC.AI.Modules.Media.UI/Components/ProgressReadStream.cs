using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace ISC.AI.Modules.Media.UI;

/// <summary>
/// Обёртка потока чтения, которая сообщает, сколько байтов уже прочитано (ADR-0040): по ней экран медиатеки
/// показывает ход загрузки многогигабайтного файла. Ничего не буферизует и не меняет данные.
/// </summary>
public sealed class ProgressReadStream : Stream
{
    private readonly Stream _inner;
    private readonly Action<long> _onProgress;
    private long _read;

    /// <param name="inner">Исходный поток (закрывается вместе с этим).</param>
    /// <param name="onProgress">Вызывается после каждого чтения с общим числом прочитанных байтов.</param>
    public ProgressReadStream(Stream inner, Action<long> onProgress)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(onProgress);
        _inner = inner;
        _onProgress = onProgress;
    }

    /// <inheritdoc />
    public override bool CanRead => true;

    /// <inheritdoc />
    public override bool CanSeek => false;

    /// <inheritdoc />
    public override bool CanWrite => false;

    /// <inheritdoc />
    public override long Length => throw new NotSupportedException();

    /// <summary>Сколько байтов прочитано.</summary>
    public override long Position
    {
        get => _read;
        set => throw new NotSupportedException();
    }

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count) => Report(_inner.Read(buffer, offset, count));

    /// <inheritdoc />
    public override int Read(Span<byte> buffer) => Report(_inner.Read(buffer));

    /// <inheritdoc />
    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        Report(await _inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false));

    /// <inheritdoc />
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        Report(await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false));

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

    private int Report(int read)
    {
        if (read > 0)
        {
            _read += read;
            _onProgress(_read);
        }

        return read;
    }
}
