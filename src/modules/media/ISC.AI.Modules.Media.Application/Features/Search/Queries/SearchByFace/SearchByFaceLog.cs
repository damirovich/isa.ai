using System;
using Microsoft.Extensions.Logging;

namespace ISC.AI.Modules.Media.Application.Features.Search;

/// <summary>Сообщения журнала поиска по лицу (LoggerMessage — CA1848; компаньон в отдельном файле).</summary>
internal static partial class SearchByFaceLog
{
    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Поиск по лицу (дело {CaseId}): копия пробы в аудит не записана — размер {Size} байт превышает предел {Limit} байт (ТБ-072).")]
    public static partial void ProbeCopySkipped(ILogger logger, int caseId, long size, long limit);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "Поиск по лицу (дело {CaseId}, основание {AuthorizationId}) завершился ошибкой; попытка фиксируется в аудите.")]
    public static partial void Failed(ILogger logger, Exception exception, int caseId, int authorizationId);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Поиск по лицу отклонён (дело {CaseId}, основание {AuthorizationId}): {Reason}")]
    public static partial void Denied(ILogger logger, int caseId, int authorizationId, string reason);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Поиск по лицу (дело {CaseId}): вырезка пробы {CropStoredFileName} после сбоя не удалена — остаётся сиротой в хранилище.")]
    public static partial void ProbeCropNotDeleted(ILogger logger, Exception exception, int caseId, string cropStoredFileName);
}
