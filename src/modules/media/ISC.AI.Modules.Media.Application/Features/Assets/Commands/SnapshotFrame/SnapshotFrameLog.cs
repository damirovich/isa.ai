using System;
using Microsoft.Extensions.Logging;

namespace ISC.AI.Modules.Media.Application.Features.Assets.Commands.SnapshotFrame;

/// <summary>
/// Сообщения журнала снимка кадра (LoggerMessage — CA1848). Только идентификатор носителя и путь временной копии
/// (имя — GUID): сведений о содержимом материалов нет (ТД-007).
/// </summary>
internal static partial class SnapshotFrameLog
{
    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Снимок кадра носителя {AssetId}: временная копия «{TempPath}» не удалена — удалится при следующем старте хоста; до этого удалите её вручную по пути из сообщения (копия материала дела, ТБ-064).")]
    public static partial void TempFileNotDeleted(ILogger logger, Exception exception, int assetId, string tempPath);
}
