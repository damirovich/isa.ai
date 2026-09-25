using System.Globalization;

namespace ISC.AI.Speech.Protocol;

/// <summary>
/// Коды выхода процесса-распознавателя (ADR-0026) — зеркало <c>WorkerExitCodes</c> утилиты (она на
/// проекты ISC.AI не ссылается, поэтому константы продублированы; менять — согласованно).
/// </summary>
public static class SpeechWorkerExitCodes
{
    /// <summary>Успех; последняя строка stdout — <c>done</c>.</summary>
    public const int Success = 0;

    /// <summary>Неверные аргументы, отсутствующий файл, вход не WAV 16 кГц моно или запись длиннее ~37 ч (предел детектора речи).</summary>
    public const int InvalidArguments = 2;

    /// <summary>Модель распознавания или детектор речи не загрузились.</summary>
    public const int ModelLoadFailed = 3;

    /// <summary>Сбой во время распознавания.</summary>
    public const int RecognitionFailed = 4;

    /// <summary>Человекочитаемое описание кода выхода для статуса носителя и журнала.</summary>
    public static string Describe(int exitCode) => exitCode switch
    {
        Success => "распознавание завершено",
        InvalidArguments => "процесс-распознаватель отверг аргументы или входной звук (ошибка вызова или подготовки WAV, или запись длиннее ~37 ч — предела детектора речи)",
        // Среда C++ названа отдельно как ВОЗМОЖНАЯ причина: поставочная сборка sherpa-onnx без синтеза речи для Windows —
        // вариант MT, среда C++ в ней вшита статически (ADR-0026, п. 7), и Redistributable ей не нужен. Но если вместо неё
        // подставлена MD-сборка, без Visual C++ Redistributable она не загружается — а снаружи это выглядит так же, как
        // отсутствие библиотеки (то же в SherpaSpeechRecognizer утилиты).
        ModelLoadFailed => "модель распознавания или детектор речи не загрузились (файл повреждён или несовместим, нет нативной библиотеки sherpa-onnx или, если подставлена MD-сборка sherpa-onnx, на Windows-сервере нет Visual C++ Redistributable x64)",
        RecognitionFailed => "сбой во время распознавания",
        _ => string.Create(CultureInfo.InvariantCulture,
            $"процесс-распознаватель аварийно завершился с кодом {exitCode} (0x{exitCode:X8}; возможна нехватка памяти или сбой нативной библиотеки)"),
    };
}
