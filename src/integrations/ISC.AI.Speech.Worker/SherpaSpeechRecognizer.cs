using System.Reflection;
using System.Runtime.InteropServices;
using SherpaOnnx;

namespace ISC.AI.Speech.Worker;

/// <summary>
/// Детектор речи Silero VAD + модель распознавания NeMo CTC (GigaAM Multilingual) через sherpa-onnx
/// (Apache-2.0; ADR-0026). Границы участков речи от детектора — это и есть таймкоды фрагментов.
/// </summary>
/// <remarks>
/// Работает только локально: модели читаются с диска по путям, заданным адаптером после проверки
/// SHA-256 (ТИ-004); сети утилита не касается (изолированный контур, ТБ-052).
/// </remarks>
internal sealed class SherpaSpeechRecognizer : IDisposable
{
    // Окно оценки энергии при поиске тихого места для разреза длинного участка — 10 мс.
    private const int QuietFrameSamples = Pcm16WaveReader.RequiredSampleRate / 100;

    // Сколько секунд перед пределом куска просматривать в поисках паузы для разреза.
    private const double QuietSearchSeconds = 3;

    // Диагностика --decode-whole: запись читается блоками по минуте (память не зависит от длины записи);
    // на границе блока кусок режется ровно — для проверки декодирования это неважно.
    private const int DecodeWholeBlockSamples = Pcm16WaveReader.RequiredSampleRate * 60;

    private readonly VoiceActivityDetector _vad;
    private readonly OfflineRecognizer _recognizer;
    private readonly int _maxPieceSamples;
    private readonly int _quietSearchSamples;

    // Сырой звук записи для добавки перед участком (pre-roll) — создаётся в Run: в --decode-whole не нужен.
    private SampleHistory? _history;

    // Конец последнего куска, поданного модели: добавка следующего участка не заходит раньше (нет двойного звука).
    private long _decodedEnd;

    private SherpaSpeechRecognizer(VoiceActivityDetector vad, OfflineRecognizer recognizer, double maxSegmentSeconds)
    {
        _vad = vad;
        _recognizer = recognizer;
        _maxPieceSamples = (int)(maxSegmentSeconds * Pcm16WaveReader.RequiredSampleRate);
        _quietSearchSamples = (int)(Math.Min(QuietSearchSeconds, maxSegmentSeconds / 2) * Pcm16WaveReader.RequiredSampleRate);
    }

    /// <summary>
    /// Загружает детектор речи и модель. Любая неудача — <see cref="ModelLoadException"/> (код выхода 3):
    /// нет нативной библиотеки, файл модели повреждён или несовместим.
    /// </summary>
    public static SherpaSpeechRecognizer Load(WorkerArguments arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        VoiceActivityDetector? vad = null;
        try
        {
            var vadConfig = new VadModelConfig
            {
                SampleRate = Pcm16WaveReader.RequiredSampleRate,
                NumThreads = 1,
                Provider = "cpu",
                Debug = 0,
            };
            // Значения и их обоснование — в VadSettings (там же они проверены тестами). MaxSpeechDuration НЕ
            // равен пределу куска: принудительный разрез детектором теряет звук на стыке, а предел куска для
            // модели без потерь держит SegmentSplitter.
            vadConfig.SileroVad.Model = arguments.VadPath;
            vadConfig.SileroVad.Threshold = VadSettings.Threshold;
            vadConfig.SileroVad.MinSilenceDuration = VadSettings.MinSilenceSeconds;
            vadConfig.SileroVad.MinSpeechDuration = VadSettings.MinSpeechSeconds;
            vadConfig.SileroVad.WindowSize = VadSettings.WindowSize;
            vadConfig.SileroVad.MaxSpeechDuration = VadSettings.MaxSpeechSeconds;

            vad = new VoiceActivityDetector(vadConfig, VadSettings.BufferSeconds);
            EnsureNativeHandle(vad, "детектор речи Silero VAD", arguments.VadPath);

            var config = new OfflineRecognizerConfig();
            config.FeatConfig.SampleRate = Pcm16WaveReader.RequiredSampleRate;
            config.FeatConfig.FeatureDim = arguments.FeatureDim;
            config.ModelConfig.NeMoCtc.Model = arguments.ModelPath;
            config.ModelConfig.Tokens = arguments.TokensPath;
            config.ModelConfig.NumThreads = arguments.Threads;
            config.ModelConfig.Provider = "cpu";
            config.ModelConfig.ModelType = "nemo_ctc";
            config.ModelConfig.Debug = 0;
            config.DecodingMethod = "greedy_search";

            var recognizer = new OfflineRecognizer(config);
            EnsureNativeHandle(recognizer, "модель распознавания", arguments.ModelPath);

            return new SherpaSpeechRecognizer(vad, recognizer, arguments.MaxSegmentSeconds);
        }
        catch (ModelLoadException)
        {
            vad?.Dispose();
            throw;
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException
                                              or SEHException or ExternalException or InvalidOperationException)
        {
            vad?.Dispose();

            // Где должны лежать нативные библиотеки — по факту раскладки (проверено 25.09.2026): RID-публикация
            // (publish-speech-worker.ps1) кладёт их в корень папки утилиты, рядом с exe; сборка разработчика без RID
            // (bin/<конфигурация>/net10.0) — в runtimes/<платформа>/native. Поставочная сборка для Windows — вариант MT
            // (среда C++ вшита, ADR-0026, п. 7); но если подставлена MD-сборка, без Visual C++ Redistributable загрузка
            // падает с тем же DllNotFoundException — поэтому эта причина названа как возможная, а не обязательная.
            throw new ModelLoadException(
                $"sherpa-onnx не загрузился: {exception.GetType().Name}: {exception.Message.TrimEnd('.')}. Проверьте: нативные "
                + "sherpa-onnx-c-api и onnxruntime на месте (в поставке publish-speech-worker.ps1 — в папке утилиты рядом с exe, "
                + "в сборке разработчика — в runtimes/<платформа>/native); файлы моделей не повреждены; если вместо штатной "
                + "MT-сборки подставлена MD-сборка sherpa-onnx — на Windows-сервере нужен Microsoft Visual C++ Redistributable "
                + "2015–2022 (x64).", exception);
        }
    }

    /// <summary>Сколько кусков подано модели (для диагностики в stderr; пустой текст в протокол не пишется).</summary>
    public int PiecesDecoded { get; private set; }

    /// <summary>
    /// Прогоняет запись: звук порциями окна детектора → участки речи → куски не длиннее предела → текст.
    /// Фрагменты пишутся в протокол по мере готовности. Возвращает длительность записи, мс.
    /// </summary>
    /// <exception cref="InputTooLongException">Запись длиннее <see cref="VadSettings.MaxInputSamples"/>.</exception>
    public long Run(Pcm16WaveReader reader, ProtocolWriter writer)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(writer);

        _history = new SampleHistory(VadSettings.HistorySamples);
        _decodedEnd = 0;

        var window = new float[VadSettings.WindowSize];
        int read;
        while ((read = reader.Read(window)) > 0)
        {
            _history.Append(window.AsSpan(0, read));

            // Номер отсчёта в нативном детекторе 32-битный (ADR-0026, VadSettings.MaxInputSamples): запись длиннее
            // предела не подаётся вовсе — иначе индекс переполнился бы в конце многочасового прогона и пропал бы
            // весь прогон. Обычно такую запись отсекает ещё адаптер и проверка длины файла до загрузки моделей
            // (WorkerApp); здесь — последний рубеж для входа, длина которого заранее неизвестна.
            if (VadSettings.ExceedsIndexLimit(reader.SamplesRead))
            {
                throw new InputTooLongException(VadSettings.TooLongMessage(reader.SamplesRead));
            }

            // Детектор копирует отсчёты к себе, поэтому буфер окна переиспользуется. Неполное последнее окно
            // детектор лишь откладывает у себя до целого окна — до модели и буфера оно само не дойдёт (см. ниже).
            _vad.AcceptWaveform(read == window.Length ? window : window[..read]);
            Drain(writer, reader.SamplesRead);
        }

        // Конец записи. Речь, идущая до последнего отсчёта (оборванное голосовое сообщение), паузой не закрыта.
        // Поэтому детектору подаётся тишина — как если бы запись продолжалась: сначала нули добивают неполное
        // последнее окно до целого, затем целые окна тишины (не меньше паузы MinSilenceDuration + 2 окна). Участок
        // закрывается штатно, и его конец совпадает с концом настоящей речи; край, захваченный тишиной, зажимается
        // по длине записи (Drain). Длительность записи — по-прежнему по reader.SamplesRead, без добавленных нулей.
        var silence = new float[VadSettings.WindowSize];
        var padding = VadSettings.PartialWindowPadding(reader.SamplesRead);
        if (padding > 0)
        {
            _vad.AcceptWaveform(silence[..padding]);
            Drain(writer, reader.SamplesRead);
        }

        for (var i = 0; i < VadSettings.TrailingSilenceWindows; i++)
        {
            _vad.AcceptWaveform(silence);
            Drain(writer, reader.SamplesRead);
        }

        // Страховка, если участок так и не закрылся (детектор всё ещё считает тишину речью). Основной путь — тишина
        // выше, а не Flush: неполное последнее окно Flush не видит, а конец участка, по разбору исходников sherpa-onnx
        // при ревью, ставит на «хвост буфера − MinSilenceDuration» (срезал бы последние 0,4 с, а участок, начавшийся
        // ближе к концу, отбросил бы целиком). На 1.13.8 среза не наблюдалось (25.09.2026, синтетическая речь,
        // оборванная на слове: конец — последнее целое окно), но при обновлении пакета на это не полагаться.
        _vad.Flush();
        Drain(writer, reader.SamplesRead);

        return SamplesToMs(reader.SamplesRead);
    }

    /// <summary>
    /// Диагностический режим <c>--decode-whole</c> (<see cref="WorkerArguments.DecodeWhole"/>): вся запись без
    /// детектора речи — блоками, каждый режется <see cref="SegmentSplitter"/> на куски не длиннее предела и
    /// подаётся модели; в протокол — обычные строки фрагментов и итог. Проверяет путь декодирования (признаки,
    /// модель, словарь) на синтетическом звуке без записей людей. Возвращает длительность записи, мс.
    /// </summary>
    public long RunWhole(Pcm16WaveReader reader, ProtocolWriter writer)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(writer);

        var block = new float[DecodeWholeBlockSamples];
        long blockStart = 0;
        int read;
        while ((read = reader.Read(block)) > 0)
        {
            Decode(block, read, blockStart, reader.SamplesRead, writer);
            blockStart += read;
        }

        return SamplesToMs(reader.SamplesRead);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _recognizer.Dispose();
        _vad.Dispose();
    }

    private void Drain(ProtocolWriter writer, long recordingSamples)
    {
        while (!_vad.IsEmpty())
        {
            var segment = _vad.Front();
            _vad.Pop();

            // Start — номер отсчёта от начала записи (int в нативном API; предел длины — VadSettings.MaxInputSamples).
            long start = segment.Start;
            var samples = segment.Samples;

            // Добавка перед участком (pre-roll): детектор срабатывает с опозданием, и тихое или короткое первое
            // слово иначе не доходит до модели. Граница — VadSettings.PreRollStart: не раньше конца предыдущего
            // куска (без двойного звука на стыке) и не раньше того, что помнит история. Таймкод начала фрагмента
            // сдвигается вместе с добавкой — он указывает на начало звука, поданного модели. Нарезка SegmentSplitter
            // и зажатие по концу записи дальше работают с участком целиком, уже с добавкой.
            if (_history is { } history)
            {
                var from = VadSettings.PreRollStart(start, _decodedEnd, history.Start, history.End);
                if (from < start)
                {
                    var preRoll = (int)(start - from);
                    var extended = new float[preRoll + samples.Length];
                    history.CopyTo(from, extended.AsSpan(0, preRoll));
                    samples.CopyTo(extended, preRoll);
                    samples = extended;
                    start = from;
                }
            }

            _decodedEnd = Math.Max(_decodedEnd, Decode(samples, samples.Length, start, recordingSamples, writer));
        }
    }

    // Режет участок на куски не длиннее предела и пишет их текст с таймкодами на шкале записи. Кусок зажимается
    // по числу настоящих отсчётов: в конце записи детектору подаётся искусственная тишина, и участок может
    // захватить её край. Модели подаётся только настоящий звук; кусок целиком из тишины не декодируется.
    // Возвращает конец последнего поданного модели куска (0 — ни одного).
    private long Decode(float[] samples, int length, long start, long recordingSamples, ProtocolWriter writer)
    {
        long decodedEnd = 0;
        foreach (var piece in SegmentSplitter.Split(samples.AsSpan(0, length), _maxPieceSamples, _quietSearchSamples, QuietFrameSamples))
        {
            if (VadSettings.ClampToRecording(start + piece.Offset, piece.Length, recordingSamples) is not { } bounds)
            {
                continue;
            }

            var realLength = (int)(bounds.End - bounds.Start);
            var text = Recognize(piece.Offset == 0 && realLength == samples.Length
                ? samples
                : samples.AsSpan(piece.Offset, realLength).ToArray());

            writer.WriteSegment(SamplesToMs(bounds.Start), SamplesToMs(bounds.End), text);
            decodedEnd = bounds.End;
        }

        return decodedEnd;
    }

    private string Recognize(float[] samples)
    {
        using var stream = _recognizer.CreateStream();
        stream.AcceptWaveform(Pcm16WaveReader.RequiredSampleRate, samples);
        _recognizer.Decode(stream);
        PiecesDecoded++;
        return stream.Result.Text;
    }

    private static long SamplesToMs(long samples) => samples * 1000 / Pcm16WaveReader.RequiredSampleRate;

    /// <summary>
    /// Нативный конструктор sherpa-onnx при неверной конфигурации НЕ бросает исключение, а пишет причину в
    /// stderr и возвращает нулевой указатель; первое же обращение к такому объекту роняет процесс без
    /// объяснений. Поэтому указатель проверяется сразу (закрытое поле <c>_handle</c> обёртки; версия пакета
    /// закреплена в Directory.Packages.props — при обновлении проверить, что поле на месте).
    /// </summary>
    private static void EnsureNativeHandle(object wrapper, string purpose, string path)
    {
        var field = wrapper.GetType().GetField("_handle", BindingFlags.Instance | BindingFlags.NonPublic);
        if (field?.GetValue(wrapper) is HandleRef handle && handle.Handle == IntPtr.Zero)
        {
            throw new ModelLoadException(
                $"sherpa-onnx не создал {purpose} из файла {Path.GetFullPath(path)}: конфигурация отвергнута "
                + "(причина — строкой выше от sherpa-onnx). Проверьте файл и его пин SHA-256.");
        }
    }
}

/// <summary>Модель или детектор речи не загрузились — процесс завершается кодом <see cref="WorkerExitCodes.ModelLoadFailed"/>.</summary>
internal sealed class ModelLoadException(string message, Exception? inner = null) : Exception(message, inner);
