namespace ISC.AI.Speech.Worker;

/// <summary>
/// Коды выхода процесса-распознавателя (ADR-0026) — часть протокола с адаптером <c>ISC.AI.Speech</c>:
/// по ним адаптер объясняет пользователю, ЧТО сломалось (настройка, модель или сама запись), не разбирая
/// текст stderr.
/// </summary>
/// <remarks>
/// Код 1 намеренно не занят: его возвращает среда .NET при необработанном исключении, и он должен
/// оставаться признаком именно аварии, а не штатного отказа.
/// </remarks>
internal static class WorkerExitCodes
{
    /// <summary>Расшифровка выполнена; последняя строка stdout — <c>done</c>.</summary>
    public const int Success = 0;

    /// <summary>
    /// Неверные аргументы командной строки, отсутствующий файл, вход не в формате WAV 16 кГц моно 16 бит или запись
    /// длиннее предела индексов детектора речи (~37 ч, <see cref="VadSettings.MaxInputSamples"/>).
    /// </summary>
    public const int InvalidArguments = 2;

    /// <summary>Модель распознавания или детектор речи не загрузились (файл повреждён, несовместим, нет нативной библиотеки).</summary>
    public const int ModelLoadFailed = 3;

    /// <summary>Сбой во время распознавания (чтение звука, детектор речи, модель, запись протокола).</summary>
    public const int RecognitionFailed = 4;
}
