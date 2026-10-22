using System.Buffers.Binary;

namespace ISC.AI.Speech.Speakers;

/// <summary>
/// Разметка дикторов по каналам стерео-записи (ADR-0062): у телефонного звонка с раздельной записью абонент слышен в
/// своём канале громче, чем в чужом. Окна по 100 мс: канал громче другого в 4 раза (на 6 дБ) и выше порога тишины —
/// говорит его абонент. Каналы-копии (моно, записанное в два канала) распознаются, и разметка отдаётся модели.
/// </summary>
/// <remarks>
/// Работает потоком по WAV 16 бит, 2 канала (адаптер готовит его ffmpeg в 8 кГц) — память не зависит от длины записи.
/// Чистая функция без процесса и диска — проверяется тестами на синтетическом звуке.
/// </remarks>
public static class StereoChannelAnalyzer
{
    /// <summary>Длина окна, мс.</summary>
    public const int WindowMs = 100;

    /// <summary>Во сколько раз энергия одного канала должна превышать другой, чтобы окно отнести к нему (≈ 6 дБ).</summary>
    public const double DominanceRatio = 4;

    /// <summary>Порог тишины: среднеквадратичный уровень окна ниже — не речь (≈ −46 дБ полной шкалы).</summary>
    public const double SilenceRms = 160;

    /// <summary>Доля окон с явным перевесом одного канала среди окон с речью, ниже которой каналы считаются копиями.</summary>
    public const double MinDominantShare = 0.6;

    /// <summary>Доля окон, которую должен «выиграть» каждый канал: иначе говорит один человек, разделять нечего.</summary>
    public const double MinChannelShare = 0.05;

    /// <summary>Разрывы реплики короче этого (тишина, перекрытие) склеиваются, мс.</summary>
    public const int BridgeMs = 300;

    /// <summary>Реплики короче этого отбрасываются (щелчки, вздохи), мс.</summary>
    public const int MinTurnMs = 200;

    /// <summary>
    /// Реплики по каналам: диктор 0 — левый канал, 1 — правый; <see langword="null"/> — каналы не раздельные (копии или
    /// говорит один), разметку нужно делать моделью.
    /// </summary>
    /// <exception cref="InvalidDataException">Не WAV PCM 16 бит, 2 канала.</exception>
    public static IReadOnlyList<SpeakerTurnSpan>? Analyze(Stream wav)
    {
        ArgumentNullException.ThrowIfNull(wav);
        var sampleRate = ReadHeader(wav);
        var frames = Math.Max(1, sampleRate * WindowMs / 1000);
        var buffer = new byte[frames * 4];

        var windows = new List<int>(); // -1 тишина, 0/1 — канал-перевес, 2 — оба (перекрытие или копии)
        int read;
        while ((read = ReadFull(wav, buffer)) > 0)
        {
            double left = 0, right = 0;
            var count = read / 4;
            for (var i = 0; i < count; i++)
            {
                double l = BinaryPrimitives.ReadInt16LittleEndian(buffer.AsSpan(i * 4, 2));
                double r = BinaryPrimitives.ReadInt16LittleEndian(buffer.AsSpan(i * 4 + 2, 2));
                left += l * l;
                right += r * r;
            }

            var rmsLeft = Math.Sqrt(left / Math.Max(1, count));
            var rmsRight = Math.Sqrt(right / Math.Max(1, count));
            windows.Add(Math.Max(rmsLeft, rmsRight) < SilenceRms ? -1
                : left > right * DominanceRatio ? 0
                : right > left * DominanceRatio ? 1
                : 2);
        }

        var voiced = windows.Count(w => w >= 0);
        var leftWins = windows.Count(w => w == 0);
        var rightWins = windows.Count(w => w == 1);
        if (voiced == 0
            || (leftWins + rightWins) < voiced * MinDominantShare
            || leftWins < voiced * MinChannelShare
            || rightWins < voiced * MinChannelShare)
        {
            return null;
        }

        return Turns(windows);
    }

    /// <summary>Реплики из размеченных окон: подряд идущие окна одного канала, короткие разрывы склеиваются.</summary>
    internal static IReadOnlyList<SpeakerTurnSpan> Turns(IReadOnlyList<int> windows)
    {
        var turns = new List<SpeakerTurnSpan>();
        var bridgeWindows = BridgeMs / WindowMs;
        int? speaker = null;
        var start = 0;
        var lastVoiced = -1;
        for (var i = 0; i < windows.Count; i++)
        {
            var w = windows[i];
            if (w is 0 or 1)
            {
                if (speaker == w && i - lastVoiced <= bridgeWindows + 1)
                {
                    lastVoiced = i;
                    continue;
                }

                Close();
                speaker = w;
                start = i;
                lastVoiced = i;
            }
        }

        Close();
        return turns;

        void Close()
        {
            if (speaker is { } s && (lastVoiced - start + 1) * WindowMs >= MinTurnMs)
            {
                turns.Add(new SpeakerTurnSpan((long)start * WindowMs, (long)(lastVoiced + 1) * WindowMs, s));
            }

            speaker = null;
        }
    }

    // Заголовок RIFF/WAVE: формат PCM 16 бит, 2 канала; поток оставляется в начале данных. Возвращает частоту.
    private static int ReadHeader(Stream wav)
    {
        var head = new byte[12];
        if (ReadFull(wav, head) != 12 || head[0] != 'R' || head[1] != 'I' || head[2] != 'F' || head[3] != 'F'
            || head[8] != 'W' || head[9] != 'A' || head[10] != 'V' || head[11] != 'E')
        {
            throw new InvalidDataException("Не WAV: нет заголовка RIFF/WAVE.");
        }

        int? sampleRate = null;
        var chunk = new byte[8];
        while (ReadFull(wav, chunk) == 8)
        {
            var id = System.Text.Encoding.ASCII.GetString(chunk, 0, 4);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(chunk.AsSpan(4));
            if (id == "fmt ")
            {
                var fmt = new byte[size];
                if (ReadFull(wav, fmt) != size || size < 16)
                {
                    throw new InvalidDataException("WAV: оборван блок fmt.");
                }

                var format = BinaryPrimitives.ReadUInt16LittleEndian(fmt);
                var channels = BinaryPrimitives.ReadUInt16LittleEndian(fmt.AsSpan(2));
                var bits = BinaryPrimitives.ReadUInt16LittleEndian(fmt.AsSpan(14));
                if (format != 1 || channels != 2 || bits != 16)
                {
                    throw new InvalidDataException($"WAV: ожидался PCM 16 бит, 2 канала; получено формат {format}, каналов {channels}, бит {bits}.");
                }

                sampleRate = (int)BinaryPrimitives.ReadUInt32LittleEndian(fmt.AsSpan(4));
                if (size % 2 == 1)
                {
                    wav.ReadByte();
                }
            }
            else if (id == "data")
            {
                return sampleRate ?? throw new InvalidDataException("WAV: блок data раньше блока fmt.");
            }
            else
            {
                // Чужие блоки (LIST и т.п.) пропускаются; размер выравнивается до чётного.
                var skip = size + (size % 2);
                if (wav.CanSeek)
                {
                    wav.Seek(skip, SeekOrigin.Current);
                }
                else
                {
                    var trash = new byte[Math.Min(skip, 64 * 1024)];
                    for (long left = skip; left > 0;)
                    {
                        var n = wav.Read(trash, 0, (int)Math.Min(trash.Length, left));
                        if (n <= 0)
                        {
                            break;
                        }

                        left -= n;
                    }
                }
            }
        }

        throw new InvalidDataException("WAV: нет блока data.");
    }

    private static int ReadFull(Stream stream, byte[] buffer)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var n = stream.Read(buffer, total, buffer.Length - total);
            if (n <= 0)
            {
                break;
            }

            total += n;
        }

        return total;
    }
}
