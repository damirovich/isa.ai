using Microsoft.ML.OnnxRuntime;

namespace ISC.AI.Vision.Onnx;

/// <summary>Общие настройки сессий ONNX Runtime.</summary>
internal static class OnnxSessions
{
    /// <summary>
    /// Логирование только ошибок: модели OpenCV Zoo экспортированы старым конвертером, и рантайм на
    /// каждую загрузку печатает десятки предупреждений про инициализаторы в графе — это шум, не дефект;
    /// в технологические логи хоста он попадать не должен (ТБ-043 — логи держим чистыми и читаемыми).
    /// </summary>
    public static SessionOptions Quiet() => new() { LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR };
}
