using System.Buffers.Binary;

namespace ISC.AI.Speech.Worker;

/// <summary>
/// Потоковое чтение WAV «16 кГц, моно, 16 бит PCM» — единственного формата, который готовит адаптер через
/// ffmpeg (ADR-0026). Отсчёты отдаются порциями: запись в часы не собирается в памяти целиком (час звука
/// во float — 230 МБ).
/// </summary>
/// <remarks>
/// ПОЧЕМУ ТОЛЬКО ОДИН ФОРМАТ. Приведение любых контейнеров и кодеков — задача ffmpeg на стороне адаптера;
/// здесь любое отклонение (другая частота, стерео, 24 бита, сжатие) означает ошибку вызова, и молча
/// «пересчитать» его значило бы получить неверные таймкоды. Поэтому — явный отказ с описанием.
/// Необязательные блоки (LIST, fact и т.п.) пропускаются. Размер блока данных 0 или 0xFFFFFFFF
/// (запись в поток без перемотки) трактуется как «до конца файла».
/// </remarks>
internal sealed class Pcm16WaveReader : IDisposable
{
    /// <summary>Требуемая частота дискретизации — её ждут и детектор речи, и модель.</summary>
    public const int RequiredSampleRate = 16000;

    private const ushort FormatPcm = 1;
    private const ushort FormatExtensible = 0xFFFE;

    private readonly Stream _stream;
    private readonly bool _leaveOpen;
    private byte[] _bytes = [];
    private long _remainingBytes;

    private Pcm16WaveReader(Stream stream, long dataBytes, bool leaveOpen)
    {
        _stream = stream;
        _remainingBytes = dataBytes;
        _leaveOpen = leaveOpen;

        // Размер из заголовка — верхняя граница (файл мог оборваться); у файла на диске длина известна точно,
        // в том числе когда в заголовке «до конца файла» (WAV больше 4 ГБ от ffmpeg — 0xFFFFFFFF).
        var available = stream.CanSeek ? Math.Max(0, stream.Length - stream.Position) : long.MaxValue;
        var bytes = Math.Min(dataBytes, available);
        ExpectedSamples = bytes == long.MaxValue ? null : bytes / 2;
    }

    /// <summary>Сколько отсчётов уже прочитано — по нему считается длительность записи.</summary>
    public long SamplesRead { get; private set; }

    /// <summary>
    /// Сколько отсчётов в записи по заголовку и размеру файла — ДО чтения; <see langword="null"/> — неизвестно
    /// (поток без перемотки и без длины в заголовке). Нужна, чтобы отказать заведомо слишком длинной записи
    /// сразу, а не после многочасового прогона (<see cref="VadSettings.MaxInputSamples"/>).
    /// </summary>
    public long? ExpectedSamples { get; }

    /// <summary>Разбирает заголовок и встаёт на начало звуковых данных.</summary>
    /// <exception cref="InvalidDataException">Файл — не WAV или не в требуемом формате (текст объясняет, что не так).</exception>
    public static Pcm16WaveReader Open(Stream stream, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(stream);

        Span<byte> header = stackalloc byte[12];
        if (stream.ReadAtLeast(header, 12, throwOnEndOfStream: false) < 12)
        {
            throw new InvalidDataException("Вход короче заголовка WAV (12 байт) — это не звуковой файл.");
        }

        var riff = header[..4];
        if (riff.SequenceEqual("RF64"u8))
        {
            throw new InvalidDataException("WAV в формате RF64 (больше 4 ГБ) не поддерживается: разбейте запись на части.");
        }

        if (!riff.SequenceEqual("RIFF"u8) || !header[8..12].SequenceEqual("WAVE"u8))
        {
            throw new InvalidDataException("Вход не является WAV (нет сигнатуры RIFF/WAVE).");
        }

        var formatSeen = false;
        Span<byte> chunkHeader = stackalloc byte[8];
        while (true)
        {
            if (stream.ReadAtLeast(chunkHeader, 8, throwOnEndOfStream: false) < 8)
            {
                throw new InvalidDataException("В WAV нет блока данных «data».");
            }

            var id = chunkHeader[..4];
            var size = BinaryPrimitives.ReadUInt32LittleEndian(chunkHeader[4..]);

            if (id.SequenceEqual("fmt "u8))
            {
                if (size is < 16 or > 1024)
                {
                    throw new InvalidDataException($"Блок формата WAV неверного размера: {size} байт.");
                }

                var fmt = new byte[size];
                stream.ReadExactly(fmt);
                ValidateFormat(fmt);
                formatSeen = true;
                SkipPadding(stream, size);
                continue;
            }

            if (id.SequenceEqual("data"u8))
            {
                if (!formatSeen)
                {
                    throw new InvalidDataException("Блок данных WAV идёт раньше блока формата «fmt ».");
                }

                // 0 и 0xFFFFFFFF пишут программы, выводящие WAV в поток без перемотки: длина неизвестна.
                var dataBytes = size is 0 or uint.MaxValue ? long.MaxValue : size;
                return new Pcm16WaveReader(stream, dataBytes, leaveOpen);
            }

            Skip(stream, size);
            SkipPadding(stream, size);
        }
    }

    /// <summary>
    /// Читает следующую порцию отсчётов в <paramref name="buffer"/> (значения в [-1, 1)). Возвращает число
    /// прочитанных отсчётов; 0 — конец данных. Оборванный последний отсчёт (нечётный байт) отбрасывается.
    /// </summary>
    public int Read(float[] buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (_remainingBytes <= 0 || buffer.Length == 0)
        {
            return 0;
        }

        var wanted = (int)Math.Min((long)buffer.Length * 2, _remainingBytes);
        if (_bytes.Length < wanted)
        {
            _bytes = new byte[wanted];
        }

        var got = _stream.ReadAtLeast(_bytes.AsSpan(0, wanted), wanted, throwOnEndOfStream: false);
        _remainingBytes = got < wanted ? 0 : _remainingBytes - got;

        var samples = got / 2;
        for (var i = 0; i < samples; i++)
        {
            buffer[i] = BinaryPrimitives.ReadInt16LittleEndian(_bytes.AsSpan(i * 2, 2)) / 32768f;
        }

        SamplesRead += samples;
        return samples;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (!_leaveOpen)
        {
            _stream.Dispose();
        }
    }

    private static void ValidateFormat(ReadOnlySpan<byte> fmt)
    {
        var format = BinaryPrimitives.ReadUInt16LittleEndian(fmt);
        var channels = BinaryPrimitives.ReadUInt16LittleEndian(fmt[2..]);
        var sampleRate = BinaryPrimitives.ReadInt32LittleEndian(fmt[4..]);
        var bits = BinaryPrimitives.ReadUInt16LittleEndian(fmt[14..]);

        if (format == FormatExtensible)
        {
            // WAVE_FORMAT_EXTENSIBLE: настоящий формат — первые два байта GUID подформата (смещение 24).
            format = fmt.Length >= 26 ? BinaryPrimitives.ReadUInt16LittleEndian(fmt[24..]) : (ushort)0;
        }

        if (format != FormatPcm || channels != 1 || sampleRate != RequiredSampleRate || bits != 16)
        {
            throw new InvalidDataException(
                $"WAV должен быть PCM 16 бит, моно, {RequiredSampleRate} Гц; получено: формат {format}, каналов {channels}, "
                + $"{sampleRate} Гц, {bits} бит. Приведите звук: ffmpeg -i <файл> -vn -ac 1 -ar 16000 -c:a pcm_s16le out.wav");
        }
    }

    // Блоки RIFF выравниваются по чётной границе: за блоком нечётной длины идёт байт-заполнитель.
    private static void SkipPadding(Stream stream, uint size)
    {
        if (size % 2 == 1)
        {
            Skip(stream, 1);
        }
    }

    private static void Skip(Stream stream, long count)
    {
        if (count <= 0)
        {
            return;
        }

        if (stream.CanSeek)
        {
            stream.Seek(count, SeekOrigin.Current);
            return;
        }

        var scratch = new byte[Math.Min(count, 64 * 1024)];
        while (count > 0)
        {
            var read = stream.Read(scratch, 0, (int)Math.Min(scratch.Length, count));
            if (read == 0)
            {
                throw new InvalidDataException("WAV оборван внутри служебного блока.");
            }

            count -= read;
        }
    }
}
