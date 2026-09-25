using System;
using Microsoft.Extensions.Logging;

namespace ISC.AI.Modules.Media.Application.Features.Transcription;

/// <summary>
/// Сообщения журнала конвейера расшифровки речи (LoggerMessage — CA1848). Текст расшифровки в журнал
/// приложения НЕ пишется никогда: это материал дела под грифом носителя (ТБ-020/032), а журнал приложения
/// решёткой не защищён — только идентификатор носителя, числа и версия модели.
/// </summary>
internal static partial class MediaTranscriptionLog
{
    [LoggerMessage(Level = LogLevel.Information,
        Message = "Расшифровка носителя {AssetId} завершена: фрагментов {Segments}, модель {ModelVersion}.")]
    public static partial void Completed(ILogger logger, int assetId, int segments, string modelVersion);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Расшифровка носителя {AssetId}: носитель не найден — конвейер не запущен.")]
    public static partial void AssetNotFound(ILogger logger, int assetId);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Расшифровка носителя {AssetId} не запускается: изображение, расшифровка неприменима.")]
    public static partial void NotApplicableToImage(ILogger logger, int assetId);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "Расшифровка носителя {AssetId} не удалась (фрагментов получено {Segments}); статус переведён в «ошибка».")]
    public static partial void Failed(ILogger logger, Exception exception, int assetId, int segments);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Расшифровка носителя {AssetId} отменена (фрагментов получено {Segments}); статус переведён в «ошибка: отменено».")]
    public static partial void Cancelled(ILogger logger, int assetId, int segments);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Расшифровка носителя {AssetId}: временный файл «{TempPath}» не удалён — удалится при следующем старте хоста; до этого удалите его вручную по пути из сообщения (копия материала дела, ТБ-064).")]
    public static partial void TempFileNotDeleted(ILogger logger, Exception exception, int assetId, string tempPath);
}
