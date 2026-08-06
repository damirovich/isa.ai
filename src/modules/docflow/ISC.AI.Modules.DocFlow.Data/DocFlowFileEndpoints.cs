using System.Text.RegularExpressions;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.DocFlow.Domain.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

namespace ISC.AI.Modules.DocFlow.Data;

/// <summary>
/// Раздача файлов модуля документооборота (этап 4.3 Э4-35, §3.3: «просмотр без скачивания»).
/// Перенос идеи <c>FileEndpoints</c> СКИД — с ОБЯЗАТЕЛЬНЫМ отличием: СКИД проверял только
/// аутентификацию, у нас перед стримом байтов проверяется ДОПУСК (гриф/подразделение документа-
/// владельца против <see cref="AccessContext"/>, fail-closed ТБ-020/021) — в СКИД грифа не было,
/// у нас файл документа несёт ту же чувствительность, что и сам документ.
/// </summary>
public static class DocFlowFileEndpoints
{
    // Строгий формат имени в хранилище (GUID + расширение) — часть defense-in-depth наравне с
    // route-констрейнтом {parentId:int} и проверкой соответствия родителя в резолвере.
    private static readonly Regex StoredFileNamePattern = new(
        @"^[0-9a-fA-F]{32}\.[A-Za-z0-9]{2,5}$", RegexOptions.Compiled);

    // БЕЗОПАСНОСТЬ (аудит 2026-08-06): сопутствующие вложения загружаются БЕЗ ограничения типа
    // (осознанно, как в СКИД, — см. FileRules) — ContentType в БД равен тому, что назвал ЗАГРУЗИВШИЙ,
    // а не проверенному содержимому. Если тип не в этом списке, отдаём принудительно как вложение
    // (Content-Disposition: attachment) НЕЗАВИСИМО от параметра download — иначе прямая навигация на
    // URL (запасная ссылка «Просмотр» для нераспознанных форматов) отрисовала бы, например, text/html
    // с same-origin XSS. На клиентский предпросмотр (docxPreview.js) не влияет: там байты берутся
    // через fetch(), а Content-Disposition управляет только НАВИГАЦИЕЙ браузера, не fetch-запросами.
    private static readonly HashSet<string> SafeInlineContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "application/pdf",
        "image/png", "image/jpeg", "image/gif", "image/bmp", "image/webp",
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        "application/msword",
    };

    /// <summary>Маршрут <c>GET /docflow/files/{category}/{parentId}/{storedFileName}</c>.</summary>
    public static void MapDocFlowFileEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/docflow/files/{category}/{parentId:int}/{storedFileName}", ServeFileAsync)
            .RequireAuthorization()
            .WithName("DocFlowFiles");
    }

    private static async Task<IResult> ServeFileAsync(
        HttpContext httpContext,
        [FromRoute] string category,
        [FromRoute] int parentId,
        [FromRoute] string storedFileName,
        [FromServices] IDocumentFileAccess fileAccess,
        [FromServices] IDocFlowFileStorage storage,
        [FromServices] IAccessContextProvider accessProvider,
        [FromServices] IAuditWriter auditWriter,
        [FromServices] ILogger<DocFlowFileEndpointsCategory> logger,
        CancellationToken cancellationToken,
        [FromQuery(Name = "download")] bool download = false)
    {
        // Второй рубеж против MIME-confusion (аудит 2026-08-06): браузер не должен «угадывать» тип
        // по содержимому вместо заявленного Content-Type.
        httpContext.Response.Headers.Append("X-Content-Type-Options", "nosniff");

        if (!IsKnownCategory(category) || !StoredFileNamePattern.IsMatch(storedFileName))
        {
            DocFlowFileEndpointsLog.RejectedBadRoute(logger, category, storedFileName);
            return Results.NotFound();
        }

        var file = await fileAccess.ResolveAsync(category, parentId, storedFileName, cancellationToken);
        if (file is null)
        {
            // Причина наружу НЕ различается (единый 404) — но в лог, для диагностики, пишем: либо строки
            // с таким именем нет вообще, либо она принадлежит ДРУГОМУ parentId (см. IDocumentFileAccess).
            DocFlowFileEndpointsLog.RejectedNotResolved(logger, category, parentId, storedFileName);
            return Results.NotFound();
        }

        // Fail-closed (ТБ-020/021): без контекста или вне допуска — 404, не 403 (не подтверждаем
        // сам факт существования файла тому, кому его видеть нельзя).
        AccessContext access;
        try
        {
            access = await accessProvider.GetCurrentAsync(cancellationToken);
        }
        catch (AccessContextRequiredException)
        {
            DocFlowFileEndpointsLog.RejectedNoAccessContext(logger, category, parentId, storedFileName);
            return Results.NotFound();
        }

        if (access.MaxClassification < file.Classification || !access.AllowedDivisions.Contains(file.DivisionId))
        {
            DocFlowFileEndpointsLog.RejectedOutsideClearance(
                logger, access.SubjectId, access.MaxClassification,
                string.Join(",", access.AllowedDivisions), file.Classification, file.DivisionId);
            return Results.NotFound();
        }

        Stream stream;
        try
        {
            stream = await storage.OpenReadAsync(storedFileName, category, file.SubPath, cancellationToken);
        }
        catch (FileNotFoundException)
        {
            DocFlowFileEndpointsLog.RejectedMissingOnDisk(logger, category, file.SubPath, storedFileName);
            return Results.NotFound();
        }

        // Просмотр (ТБ-030): читаем содержимое документа — тот же класс события, что и поиск/просмотр
        // фрагмента; гриф записи — гриф файла (не ниже, ТБ-032).
        await auditWriter.WriteAsync(
            new AuditEntry(
                AuditAction.View, file.Classification, access.NumericSubjectId,
                ObjectRef: $"docflow:file:{category}:{parentId}:{storedFileName}"),
            cancellationToken);

        // Типы вне allowlist'а — принудительно вложением, даже если download=false не запрашивал этого
        // (см. комментарий у SafeInlineContentTypes).
        var attachToResponse = download || !SafeInlineContentTypes.Contains(file.ContentType);
        return Results.File(
            stream, file.ContentType, fileDownloadName: attachToResponse ? file.FileName : null,
            enableRangeProcessing: true);
    }

    private static bool IsKnownCategory(string category) => category is
        FileCategories.Documents or FileCategories.Attachments
        or FileCategories.StatusHistory or FileCategories.DeadlineExtensions
        or FileCategories.Comments;
}

/// <summary>Категория логгера эндпоинтов раздачи (DI-якорь для <see cref="ILogger{TCategoryName}"/>).</summary>
public sealed class DocFlowFileEndpointsCategory;

/// <summary>
/// Строго-типизированные лог-сообщения раздачи файлов (LoggerMessage — CA1848). Клиенту ВСЕГДА уходит
/// единый 404 (причина не различается наружу) — эти записи только в серверный лог, для диагностики.
/// </summary>
internal static partial class DocFlowFileEndpointsLog
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "Раздача файла: неизвестная категория «{Category}» или неверный формат имени «{StoredFileName}»")]
    public static partial void RejectedBadRoute(ILogger logger, string category, string storedFileName);

    [LoggerMessage(Level = LogLevel.Information, Message = "Раздача файла: не найден — категория «{Category}», parentId={ParentId}, имя «{StoredFileName}» (нет строки ИЛИ принадлежит другому родителю)")]
    public static partial void RejectedNotResolved(ILogger logger, string category, int parentId, string storedFileName);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Раздача файла: нет контекста доступа — категория «{Category}», parentId={ParentId}, имя «{StoredFileName}»")]
    public static partial void RejectedNoAccessContext(ILogger logger, string category, int parentId, string storedFileName);

    [LoggerMessage(Level = LogLevel.Information, Message = "Раздача файла: вне допуска — субъект {SubjectId} (гриф≤{MaxClassification}, подразделения [{AllowedDivisions}]) против файла (гриф={FileClassification}, подразделение={FileDivisionId})")]
    public static partial void RejectedOutsideClearance(
        ILogger logger, string subjectId, short maxClassification, string allowedDivisions,
        short fileClassification, int fileDivisionId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Раздача файла: строка в БД есть, а байтов на диске нет — категория «{Category}», подпуть «{SubPath}», имя «{StoredFileName}»")]
    public static partial void RejectedMissingOnDisk(ILogger logger, string category, string subPath, string storedFileName);
}
