using System;
using Microsoft.Extensions.Logging;

namespace ISC.AI.Modules.Media.Application.Features.Maintenance;

/// <summary>
/// Сообщения журнала обслуживания пакета «Медиа» при старте хоста (LoggerMessage — CA1848). Только пути
/// временных файлов (имена — GUID) и числа: сведений о содержимом материалов здесь нет.
/// </summary>
internal static partial class MediaMaintenanceLog
{
    [LoggerMessage(Level = LogLevel.Information,
        Message = "Уборка при старте: удалено временных копий материалов, оставшихся от прежних запусков: {Count}.")]
    public static partial void TempFilesRemoved(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Уборка при старте: временный файл «{TempPath}» не удалён — удалите его вручную (копия материала дела, ТБ-064).")]
    public static partial void TempFileNotRemoved(ILogger logger, Exception exception, string tempPath);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Уборка при старте: каталог «{Folder}» (шаблон {Pattern}) не прочитан — оставшиеся в нём временные копии удалите вручную.")]
    public static partial void TempFolderNotListed(ILogger logger, Exception exception, string folder, string pattern);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Уборка временных копий при старте не выполнена — старт не прерван; проверьте каталог {Folder} вручную.")]
    public static partial void TempSweepFailed(ILogger logger, Exception exception, string folder);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "При старте прерванные перезапуском работы переведены в «ошибка»: расшифровок {Transcriptions}, индексаций лиц {Indexings}. Повтор — кнопками в карточке носителя.")]
    public static partial void InterruptedRecovered(ILogger logger, int transcriptions, int indexings);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "Восстановление статусов носителей при старте не выполнено (БД недоступна?) — старт не прерван; носители, прерванные перезапуском, остаются «в очереди / в работе».")]
    public static partial void RecoveryFailed(ILogger logger, Exception exception);
}
