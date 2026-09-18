using System.Text.RegularExpressions;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Enums;
using ISC.AI.Abstractions.Security;
using ISC.AI.Abstractions.Storage;
using ISC.AI.Modules.Media.Domain.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

namespace ISC.AI.Modules.Media.Data;

/// <summary>
/// Раздача файлов пакета «Медиа» (ТС-010, ТБ-073): исходники носителей, вырезки лиц для показа в
/// выдаче и вырезки проб поисковых сессий (категория <c>media-probes</c>; сегмент «носитель» маршрута — идентификатор сессии). Перед стримом байтов проверяется ДОПУСК субъекта против грифа/подразделения носителя
/// (fail-closed ТБ-020/021) — биометрический материал несёт ту же чувствительность, что и сам носитель —
/// и ПОВЕРХ него область дел субъекта через порт профиля <see cref="ICaseScope"/> (ТБ-071).
/// Причина отказа наружу не различается: единый 404 (не подтверждаем существование файла тому, кому
/// его видеть нельзя).
/// </summary>
public static class MediaFileEndpoints
{
    // Строгий формат имени в хранилище (GUID + расширение) — часть defense-in-depth наравне с
    // route-констрейнтом {assetId:int} и проверкой принадлежности в резолвере.
    private static readonly Regex StoredFileNamePattern = new(
        @"^[0-9a-fA-F]{32}\.[A-Za-z0-9]{2,5}$", RegexOptions.Compiled);

    // Только типы, которые безопасно показывать inline; всё прочее — принудительно вложением
    // (MIME-confusion: same-origin XSS через text/html).
    private static readonly HashSet<string> SafeInlineContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/png", "image/jpeg", "image/gif", "image/bmp", "image/webp",
        "video/mp4", "video/webm",
    };

    /// <summary>
    /// Маршрут <see cref="MediaFileRoutes.Template"/> (<c>GET /media/files/{category}/{assetId}/{storedFileName}</c>;
    /// для <c>media-probes</c> второй сегмент — идентификатор сессии). Шаблон — из общего источника в Domain,
    /// тем же пользуется UI при построении ссылок.
    /// </summary>
    public static void MapMediaFileEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(MediaFileRoutes.Template, ServeFileAsync)
            .RequireAuthorization()
            .WithName("MediaFiles");
    }

    private static async Task<IResult> ServeFileAsync(
        HttpContext httpContext,
        [FromRoute] string category,
        [FromRoute] int assetId,
        [FromRoute] string storedFileName,
        [FromServices] IMediaFileAccess fileAccess,
        [FromServices] ICaseScope caseScope,
        [FromServices] IFileStorage storage,
        [FromServices] IAccessContextProvider accessProvider,
        [FromServices] IAuditWriter auditWriter,
        [FromServices] MediaViewAuditThrottle viewAudit,
        [FromServices] ILogger<MediaFileEndpointsCategory> logger,
        CancellationToken cancellationToken)
    {
        httpContext.Response.Headers.Append("X-Content-Type-Options", "nosniff");

        if (category is not (MediaFileCategories.Originals or MediaFileCategories.FaceCrops or MediaFileCategories.Probes)
            || !StoredFileNamePattern.IsMatch(storedFileName))
        {
            MediaFileEndpointsLog.RejectedBadRoute(logger, category, storedFileName);
            return Results.NotFound();
        }

        var file = await fileAccess.ResolveAsync(category, assetId, storedFileName, cancellationToken);
        if (file is null)
        {
            MediaFileEndpointsLog.RejectedNotResolved(logger, category, assetId, storedFileName);
            return Results.NotFound();
        }

        // Fail-closed (ТБ-020/021): без контекста или вне допуска — 404, не 403.
        AccessContext access;
        try
        {
            access = await accessProvider.GetCurrentAsync(cancellationToken);
        }
        catch (AccessContextRequiredException)
        {
            MediaFileEndpointsLog.RejectedNoAccessContext(logger, category, assetId, storedFileName);
            return Results.NotFound();
        }

        // Тот же floor, что и в поиске (BaselineAccess): гриф ≤ допуск ∧ подразделение ∈ разрешённых.
        if (!BaselineAccess.Filter<MediaFileDescriptor>(access).Compile()(file))
        {
            MediaFileEndpointsLog.RejectedOutsideClearance(
                logger, access.SubjectId, access.MaxClassification, file.Classification, file.DivisionId);
            return Results.NotFound();
        }

        // ПОВЕРХ floor — область дел субъекта (ТБ-071, ТФ-ДЕЛ-03): носитель/вырезка — по привязке носителя к
        // доступным делам, вырезка пробы — по делу сессии (CaseRef). Иначе следователь того же подразделения
        // перебором id читал бы материалы чужих дел, а субъект без роли профиля — всё в допуске
        // (default-deny, ТБ-012/021). Наружу — тот же единый 404.
        var withinCases = category == MediaFileCategories.Probes
            ? file.CaseRef is { } caseRef && await caseScope.GetCaseAsync(caseRef, access, cancellationToken) is not null
            : await caseScope.IsAssetAccessibleAsync(assetId, access, cancellationToken);
        if (!withinCases)
        {
            MediaFileEndpointsLog.RejectedOutsideCases(logger, access.SubjectId, category, assetId, storedFileName);
            return Results.NotFound();
        }

        Stream stream;
        try
        {
            stream = await storage.OpenReadAsync(storedFileName, category, file.SubPath, cancellationToken);
        }
        catch (FileNotFoundException)
        {
            MediaFileEndpointsLog.RejectedMissingOnDisk(logger, category, file.SubPath, storedFileName);
            return Results.NotFound();
        }

        // Просмотр биометрического материала — аудируемое событие (ТБ-030/072); гриф записи — гриф носителя.
        // Одна выдача субъекту — одна запись: продолжения Range-запросов (воспроизведение/перемотка видео)
        // и повторные загрузки той же вырезки в окне не множат журнал (MediaViewAuditThrottle); проверка
        // допуска выше выполнена для каждого запроса.
        var objectRef = $"media:file:{category}:{assetId}:{storedFileName}";
        if (viewAudit.ShouldAudit(access.NumericSubjectId, objectRef))
        {
            await auditWriter.WriteAsync(
                new AuditEntry(
                    AuditAction.View, file.Classification, access.NumericSubjectId,
                    ObjectRef: objectRef,
                    DivisionId: file.DivisionId),
                cancellationToken);
        }

        var attach = !SafeInlineContentTypes.Contains(file.ContentType);
        return Results.File(
            stream, file.ContentType, fileDownloadName: attach ? storedFileName : null,
            enableRangeProcessing: true);
    }
}

/// <summary>Категория логгера эндпоинтов раздачи (DI-якорь для <see cref="ILogger{TCategoryName}"/>).</summary>
public sealed class MediaFileEndpointsCategory;

/// <summary>Строго-типизированные лог-сообщения раздачи (LoggerMessage — CA1848); клиенту всегда единый 404.</summary>
internal static partial class MediaFileEndpointsLog
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "Медиа: неизвестная категория «{Category}» или неверный формат имени «{StoredFileName}»")]
    public static partial void RejectedBadRoute(ILogger logger, string category, string storedFileName);

    [LoggerMessage(Level = LogLevel.Information, Message = "Медиа: файл {Category}/{AssetId}/{StoredFileName} не разрешён (нет строки или чужой носитель)")]
    public static partial void RejectedNotResolved(ILogger logger, string category, int assetId, string storedFileName);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Медиа: файл {Category}/{AssetId}/{StoredFileName} запрошен без контекста доступа")]
    public static partial void RejectedNoAccessContext(ILogger logger, string category, int assetId, string storedFileName);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Медиа: субъект {SubjectId} (допуск {MaxClassification}) вне допуска к файлу (гриф {Classification}, подразделение {DivisionId})")]
    public static partial void RejectedOutsideClearance(ILogger logger, string subjectId, short maxClassification, short classification, int divisionId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Медиа: субъект {SubjectId} запросил файл {Category}/{AssetId}/{StoredFileName} вне дел субъекта (ТБ-071)")]
    public static partial void RejectedOutsideCases(ILogger logger, string subjectId, string category, int assetId, string storedFileName);

    [LoggerMessage(Level = LogLevel.Error, Message = "Медиа: файл {Category}/{SubPath}/{StoredFileName} есть в БД, но отсутствует на диске")]
    public static partial void RejectedMissingOnDisk(ILogger logger, string category, string subPath, string storedFileName);
}
