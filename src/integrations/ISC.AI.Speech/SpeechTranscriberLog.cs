using System;
using Microsoft.Extensions.Logging;

namespace ISC.AI.Speech;

/// <summary>
/// Сообщения журнала интеграции распознавания речи (LoggerMessage — CA1848). Только пути и служебные
/// сведения — ни текста расшифровки, ни метаданных записи (ТД-007).
/// </summary>
internal static partial class SpeechTranscriberLog
{
    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Временный каталог расшифровки не удалён: {WorkDir}. Повтор — перед следующей расшифровкой и при старте хоста (ТБ-064).")]
    public static partial void WorkDirNotDeleted(ILogger logger, Exception exception, string workDir);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Уборка остатков расшифровки в {WorkRoot} не завершена.")]
    public static partial void SweepFailed(ILogger logger, Exception exception, string workRoot);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Остаток прошлого прогона расшифровки не удалён: {WorkDir}. Повтор — перед следующей расшифровкой и при старте хоста (ТБ-064).")]
    public static partial void StaleWorkDirNotDeleted(ILogger logger, Exception exception, string workDir);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Удалены остатки прошлых прогонов расшифровки: {Count} в {WorkRoot} (ТБ-064).")]
    public static partial void StaleWorkDirsDeleted(ILogger logger, int count, string workRoot);
}
