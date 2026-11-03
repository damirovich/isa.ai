using Microsoft.ML.OnnxRuntime;

namespace ISC.AI.Vision.Onnx;

/// <summary>Общие настройки сессий ONNX Runtime.</summary>
internal static class OnnxSessions
{
    /// <summary>
    /// Переменная окружения, которая выключает телеметрию ONNX Runtime под Linux. Библиотека читает её один раз, при
    /// создании своего окружения, и только из окружения процесса: <c>Environment.SetEnvironmentVariable</c> под Linux
    /// до неё не доходит, а <c>OrtEnv.DisableTelemetryEvents()</c> обращений в сеть не прекращает (стенд, 03.11.2026).
    /// Поэтому её задаёт служба (<c>deploy/linux/package/systemd/isc-ai.service</c>), а здесь — только проверка.
    /// </summary>
    public const string TelemetryOptOutVariable = "ORT_DISABLE_TELEMETRY";

    /// <summary>
    /// Логирование только ошибок: модели OpenCV Zoo экспортированы старым конвертером, и рантайм на
    /// каждую загрузку печатает десятки предупреждений про инициализаторы в графе — это шум, не дефект;
    /// в технологические логи хоста он попадать не должен (ТБ-043 — логи держим чистыми и читаемыми).
    /// Вызывается перед каждой загрузкой модели, поэтому здесь же — проверка выключенной телеметрии.
    /// </summary>
    public static SessionOptions Quiet()
    {
        EnsureTelemetryDisabled(OperatingSystem.IsWindows(), Environment.GetEnvironmentVariable(TelemetryOptOutVariable));
        return new() { LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR };
    }

    /// <summary>
    /// Fail-closed: под Linux модель не загружается, пока у процесса не выключена телеметрия ONNX Runtime.
    /// Инвариант: в режимном контуре ничего не передаётся вовне (ТБ-052), телеметрия моделей и инструментов
    /// отключается (ТБ-043). ONNX Runtime 1.30 под Linux сама отправляет телеметрию на сервер Microsoft
    /// (mobile.events.data.microsoft.com): без сети это повторяющиеся запросы к DNS контура, при доступной сети —
    /// отправка. Под Windows у неё нет своего канала (события ETW, ими управляют политики Windows), проверка не нужна.
    /// </summary>
    /// <param name="isWindows">Процесс работает под Windows.</param>
    /// <param name="optOut">Значение <see cref="TelemetryOptOutVariable"/> в окружении процесса.</param>
    /// <exception cref="InvalidOperationException">Linux и телеметрия не выключена.</exception>
    internal static void EnsureTelemetryDisabled(bool isWindows, string? optOut)
    {
        if (isWindows || optOut == "1")
        {
            return;
        }

        throw new InvalidOperationException(
            $"Распознавание лиц не запущено: у процесса не выключена телеметрия ONNX Runtime, а в изолированном контуре "
            + $"она обращалась бы вовне (ТБ-043, ТБ-052). Задайте переменную окружения {TelemetryOptOutVariable}=1 "
            + "до запуска процесса. Служба isc-ai.service задаёт её сама — эта ошибка значит, что программа запущена "
            + "в обход службы или переменная удалена через systemctl edit.");
    }
}
