namespace ISC.AI.Vision.Onnx.Video;

/// <summary>
/// Разрезает поток MJPEG (последовательность JPEG-кадров, как отдаёт <c>ffmpeg -f image2pipe -c:v mjpeg</c>)
/// на отдельные кадры по маркерам SOI (FF D8) и EOI (FF D9). Чистая функция над байтами — тестируется
/// без ffmpeg. Внутри сжатых данных JPEG байт FF экранируется (FF 00) либо образует маркеры RSTn,
/// поэтому FF D9 внутри кадра не встречается; вложенные превью EXIF ffmpeg не пишет.
/// </summary>
public sealed class MjpegStreamSplitter : IDisposable
{
    private readonly MemoryStream _buffer = new();

    /// <inheritdoc />
    public void Dispose() => _buffer.Dispose();

    /// <summary>Добавляет очередной кусок потока и возвращает завершённые кадры (может быть пусто).</summary>
    public IReadOnlyList<byte[]> Push(ReadOnlySpan<byte> chunk)
    {
        _buffer.Write(chunk);
        var frames = new List<byte[]>();
        var data = _buffer.GetBuffer().AsSpan(0, (int)_buffer.Length);
        var consumed = 0;

        while (true)
        {
            var start = IndexOfMarker(data, consumed, 0xD8);
            if (start < 0)
            {
                consumed = data.Length; // мусор до первого SOI отбрасываем
                break;
            }

            var end = IndexOfMarker(data, start + 2, 0xD9);
            if (end < 0)
            {
                consumed = start; // кадр ещё не дочитан — ждём следующего куска
                break;
            }

            frames.Add(data.Slice(start, end + 2 - start).ToArray());
            consumed = end + 2;
        }

        // Сдвигаем недочитанный хвост в начало буфера.
        var remaining = data.Length - consumed;
        if (remaining > 0)
        {
            data.Slice(consumed, remaining).CopyTo(_buffer.GetBuffer());
        }

        _buffer.SetLength(remaining);
        return frames;
    }

    private static int IndexOfMarker(ReadOnlySpan<byte> data, int from, byte second)
    {
        for (var i = from; i + 1 < data.Length; i++)
        {
            if (data[i] == 0xFF && data[i + 1] == second)
            {
                return i;
            }
        }

        return -1;
    }
}
