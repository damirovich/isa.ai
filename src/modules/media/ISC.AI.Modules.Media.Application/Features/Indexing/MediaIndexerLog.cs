using System;
using Microsoft.Extensions.Logging;

namespace ISC.AI.Modules.Media.Application.Features.Indexing;

/// <summary>Сообщения журнала конвейера индексации носителя (LoggerMessage — CA1848; компаньон в отдельном файле).</summary>
internal static partial class MediaIndexerLog
{
    [LoggerMessage(Level = LogLevel.Information,
        Message = "Индексация носителя {AssetId} завершена: кадров {Frames}, лиц {Faces}, отклонено по качеству {Rejected}.")]
    public static partial void Completed(ILogger logger, int assetId, int frames, int faces, int rejected);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Индексация носителя {AssetId}: носитель не найден — конвейер не запущен.")]
    public static partial void AssetNotFound(ILogger logger, int assetId);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "Индексация носителя {AssetId} не удалась (кадров обработано {Frames}); статус носителя переведён в «ошибка».")]
    public static partial void Failed(ILogger logger, Exception exception, int assetId, int frames);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Индексация носителя {AssetId}: временный файл «{TempPath}» не удалён — подберёт уборка.")]
    public static partial void TempFileNotDeleted(ILogger logger, Exception exception, int assetId, string tempPath);
}
