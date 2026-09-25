using System.Globalization;

namespace ISC.AI.Speech.Worker;

/// <summary>
/// Настройки детектора речи Silero VAD и чистая арифметика вокруг него (ADR-0026): хвост тишины в конце
/// записи, зажатие таймкодов по длине записи и предел длины записи по разрядности индексов нативного
/// детектора. Здесь нет типов sherpa-onnx — всё проверяется юнит-тестами без нативной библиотеки и моделей.
/// </summary>
internal static class VadSettings
{
    /// <summary>Частота дискретизации детектора — та же, что у входного WAV.</summary>
    public const int SampleRate = Pcm16WaveReader.RequiredSampleRate;

    /// <summary>Окно Silero VAD при 16 кГц — 512 отсчётов (32 мс): так модель детектора обучена.</summary>
    public const int WindowSize = 512;

    /// <summary>Порог вероятности речи.</summary>
    public const float Threshold = 0.5f;

    /// <summary>
    /// Пауза 0,4 с завершает участок: короче — фразы дробятся посреди речи, длиннее — участки слипаются и
    /// таймкод фрагмента хуже указывает на место слова в записи.
    /// </summary>
    public const float MinSilenceSeconds = 0.4f;

    /// <summary>Пауза <see cref="MinSilenceSeconds"/> в отсчётах — так её считает нативный детектор.</summary>
    public const int MinSilenceSamples = (int)(MinSilenceSeconds * SampleRate);

    /// <summary>Участок короче 0,25 с речью не считается (щелчки, отдельные шумы).</summary>
    public const float MinSpeechSeconds = 0.25f;

    /// <summary>
    /// Длина участка, после которой детектор переходит в «режим длинной речи» (порог 0,9, пауза 0,1 с) и режет
    /// участок принудительно. НАМЕРЕННО большая: на таком стыке следующий участок начинается не там, где
    /// кончился предыдущий, и десятки миллисекунд звука не попадают ни в один фрагмент — для дословной
    /// расшифровки это выпавшие короткие слова. Предел куска для модели держит <see cref="SegmentSplitter"/>:
    /// он режет встык по самому тихому месту, без потерь. Ноль задать нельзя: C API sherpa-onnx подставляет
    /// вместо нуля своё умолчание 20 с (<c>SHERPA_ONNX_OR</c>; исходников пакета в контуре нет — по разбору при
    /// ревью) — ровно то, от чего здесь уходим.
    /// </summary>
    public const float MaxSpeechSeconds = 600;

    /// <summary>
    /// Ёмкость кольцевого буфера детектора, секунды: самый длинный участок (<see cref="MaxSpeechSeconds"/>) с
    /// запасом, но не меньше минуты. Нативный буфер умеет расти и сам, но каждый раз пишет предупреждение в stderr.
    /// </summary>
    public static readonly float BufferSeconds = Math.Max(60f, MaxSpeechSeconds + 10);

    /// <summary>
    /// Окна тишины, подаваемые детектору после конца записи: пауза <see cref="MinSilenceSamples"/>, округлённая
    /// вверх до целых окон, плюс 3 окна — одно на запаздывание спада вероятности речи и два запаса (не меньше
    /// «MinSilenceDuration + 2 окна»). При 0,4 с — 16 окон, 0,512 с.
    /// </summary>
    public const int TrailingSilenceWindows = (MinSilenceSamples + WindowSize - 1) / WindowSize + 3;

    /// <summary>
    /// Звук перед началом участка, добавляемый к нему (pre-roll), — 0,25 с. Детектор срабатывает с опозданием на
    /// тихом или коротком начале речи (глухой согласный, предлог «в», «с»): без добавки такое первое слово не
    /// доходит до модели (проверено 25.09.2026 на синтетической фразе: без добавки «в понедельник» →
    /// «понедельник»). Больше не нужно: участок и так начинается на 2 окна + <see cref="MinSpeechSeconds"/> раньше
    /// срабатывания, а лишняя тишина перед словом лишь сдвигает таймкод.
    /// </summary>
    public const int PreRollSamples = SampleRate / 4;

    /// <summary>
    /// Сколько секунд сырого звука держит своя история утилиты (<see cref="SampleHistory"/>) ради добавки перед
    /// участком. Детектор отдаёт участок только после его конца — самое позднее через <see cref="MaxSpeechSeconds"/>
    /// речи, паузу <see cref="MinSilenceSeconds"/> и несколько окон, — поэтому история длиной с буфер детектора и
    /// запасом 5 с ещё помнит звук перед его началом. Память: 615 с × 16 000 × 4 байта ≈ 39,4 МБ, выделяются один раз
    /// на прогон (столько же занимает нативный буфер самого детектора). Если участок всё же длиннее (детектор так и
    /// не закрыл сплошной шум), добавка сокращается до того, что осталось в истории, — вплоть до нуля.
    /// </summary>
    public static readonly float HistorySeconds = BufferSeconds + 5;

    /// <summary>Ёмкость истории, отсчёты (<see cref="HistorySeconds"/>).</summary>
    public static readonly int HistorySamples = (int)(HistorySeconds * SampleRate);

    /// <summary>
    /// Запас до <see cref="int.MaxValue"/>: ёмкость буфера детектора (его внутренние расчёты вида «индекс +
    /// размер»), наибольший хвост тишины и ещё одно окно.
    /// </summary>
    public static readonly long IndexReserveSamples =
        (long)(BufferSeconds * SampleRate) + (WindowSize - 1) + (long)TrailingSilenceWindows * WindowSize + WindowSize;

    /// <summary>
    /// Наибольшая длина записи, отсчёты (~37,1 ч при 16 кГц). Номер отсчёта в нативном детекторе
    /// (<c>SpeechSegment.Start</c>, индексы кольцевого буфера) — 32-битный; дальше он переполнился бы, и после
    /// многочасового прогона процесс упал бы или выдал отрицательные таймкоды — весь прогон пропал бы.
    /// Первый рубеж — предел длительности в адаптере (<c>Speech:MaxDurationHours</c>, не больше 36 ч); этот —
    /// второй, на случай вызова утилиты в обход адаптера.
    /// </summary>
    public static readonly long MaxInputSamples = int.MaxValue - IndexReserveSamples;

    /// <summary>
    /// Сколько нулей добить к последнему неполному окну: нативный детектор копит отсчёты, пока не наберётся
    /// целое окно, и остаток короче окна до модели детектора и его буфера не доходит вовсе.
    /// </summary>
    /// <param name="samplesFed">Сколько отсчётов записи уже подано детектору.</param>
    public static int PartialWindowPadding(long samplesFed)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(samplesFed);
        return (int)((WindowSize - samplesFed % WindowSize) % WindowSize);
    }

    /// <summary>
    /// Запись длиннее предела <see cref="MaxInputSamples"/> — детектору её подавать нельзя
    /// (см. <see cref="InputTooLongException"/>).
    /// </summary>
    public static bool ExceedsIndexLimit(long samples) => samples > MaxInputSamples;

    /// <summary>Текст отказа по длине записи — для stderr (его показывает адаптер).</summary>
    /// <param name="samples">Длина записи (или сколько уже прочитано), отсчёты.</param>
    public static string TooLongMessage(long samples) => string.Create(CultureInfo.InvariantCulture,
        $"Запись длиннее ~{MaxInputSamples / (double)SampleRate / 3600:0} ч ({samples} отсчётов при {SampleRate} Гц, предел "
        + $"{MaxInputSamples}): номера отсчётов в нативном детекторе речи sherpa-onnx 32-битные и дальше переполнились бы. "
        + $"Разбейте запись на части не длиннее 36 ч.");

    /// <summary>
    /// Откуда начинать участок с добавкой <see cref="PreRollSamples"/>: на добавку раньше его начала, но не раньше
    /// конца предыдущего поданного модели куска (иначе звук на стыке прозвучал бы в двух фрагментах — в дословной
    /// расшифровке это выглядело бы как сказанное дважды), не раньше начала записи и не раньше самого старого
    /// отсчёта, который ещё помнит история. Если звука до начала участка в истории нет — без добавки.
    /// </summary>
    /// <param name="segmentStart">Начало участка от детектора, отсчёты от начала записи.</param>
    /// <param name="previousEnd">Конец предыдущего куска, поданного модели (0 — кусков ещё не было).</param>
    /// <param name="historyStart">Самый старый отсчёт в истории (<see cref="SampleHistory.Start"/>).</param>
    /// <param name="historyEnd">Номер следующего за последним отсчётом в истории (<see cref="SampleHistory.End"/>).</param>
    /// <returns>Начало участка с добавкой — в [нижняя граница, <paramref name="segmentStart"/>].</returns>
    public static long PreRollStart(long segmentStart, long previousEnd, long historyStart, long historyEnd)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(segmentStart);

        if (segmentStart > historyEnd)
        {
            return segmentStart;
        }

        var from = Math.Max(Math.Max(segmentStart - PreRollSamples, 0), Math.Max(previousEnd, historyStart));
        return Math.Min(from, segmentStart);
    }

    /// <summary>
    /// Границы куска на шкале записи: конец — не дальше последнего настоящего отсчёта. После конца записи
    /// детектору подаётся искусственная тишина (<see cref="TrailingSilenceWindows"/>), и закрытый ею участок
    /// может захватить её край; таймкоды фрагментов не должны выходить за длительность записи.
    /// </summary>
    /// <param name="start">Начало куска от начала записи, отсчёты.</param>
    /// <param name="length">Длина куска, отсчёты.</param>
    /// <param name="recordingSamples">Сколько настоящих отсчётов в записи.</param>
    /// <returns>Границы [Start, End); <see langword="null"/> — кусок целиком из добавленной тишины.</returns>
    public static (long Start, long End)? ClampToRecording(long start, int length, long recordingSamples)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(start);
        ArgumentOutOfRangeException.ThrowIfNegative(length);

        var end = Math.Min(start + length, recordingSamples);
        return end > start ? (start, end) : null;
    }
}

/// <summary>
/// Запись длиннее предела индексов нативного детектора (<see cref="VadSettings.MaxInputSamples"/>) — процесс
/// завершается кодом <see cref="WorkerExitCodes.InvalidArguments"/> (вход не годится), а не аварией.
/// </summary>
internal sealed class InputTooLongException(string message) : Exception(message);
