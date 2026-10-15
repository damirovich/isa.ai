using System.Globalization;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using ISC.AI.Abstractions.Storage;
using ISC.AI.Modules.Media.Domain.Model;
using ISC.AI.Modules.Media.Domain.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

namespace ISC.AI.Modules.Media.Data;

/// <summary>Ответ эндпоинта волны (JSON, camelCase): шаг между пиками, покрытая длительность и пики 0–255 в base64.</summary>
/// <param name="StepMs">Шаг между пиками, мс.</param>
/// <param name="DurationMs">Длительность, которую покрывает волна, мс.</param>
/// <param name="Peaks">Пики (байты 0–255) в base64 — компактнее массива чисел в JSON.</param>
public sealed record AudioWaveformResponse(int StepMs, long DurationMs, string Peaks);

/// <summary>
/// Волна аудиозаписи (ADR-0047): <c>GET /media/waveforms/{assetId}</c> отдаёт огибающую громкости для картинки под
/// проигрывателем. Проверки — те же и в том же порядке, что у кадра видео (<see cref="MediaFrameEndpoints"/>):
/// аутентификация, floor ядра по строке носителя (ТБ-020/021), область дел субъекта либо кандидат его поиска (ТБ-071,
/// ADR-0044), единый 404 на любой отказ, <c>nosniff</c>, <c>no-store</c>; плюс «только аудио».
/// </summary>
/// <remarks>
/// Волна строится ОДИН раз — ffmpeg из оригинала (<see cref="IAudioWaveformExtractor"/>) — и запоминается в строке
/// носителя (<see cref="IAudioWaveformCache"/>); следующие открытия карточки читают готовую. Оригинал — на месте через
/// <see cref="ILocalFileLocator"/> либо через временную копию, удаляемую в <c>finally</c> (как у кадра, ТБ-064).
/// Сбой построения (нет ffmpeg, файл без звука или повреждён, сервер занят) — тот же 404 и запись в журнал диагностики:
/// проигрыватель тогда показывает простую полосу. Отказ лимитера (общая политика с кадрами) — 429 без тела.
/// </remarks>
public static class MediaWaveformEndpoints
{
    /// <summary>Префикс временной копии оригинала для построения волны (каталог тот же, что у кадров).</summary>
    public const string TempFilePrefix = "waveform-";

    /// <summary>Префикс <c>ObjectRef</c> записи журнала о просмотре волны носителя: <c>media:waveform:{assetId}</c>.</summary>
    public const string AuditObjectRefPrefix = "media:waveform:";

    /// <summary>Маршрут <see cref="MediaFileRoutes.WaveformTemplate"/> под аутентификацией и лимитером процессов ffmpeg.</summary>
    public static void MapMediaWaveformEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Лимитер — общий с кадром (MediaFrameRateLimitPolicy): построение волны — тоже процесс ffmpeg.
        endpoints.MapGet(MediaFileRoutes.WaveformTemplate, ServeWaveformAsync)
            .RequireAuthorization()
            .RequireRateLimiting(MediaFrameRateLimitPolicy.PolicyName)
            .WithName("MediaWaveforms");
    }

    /// <summary>
    /// Обработчик волны. Публичный, чтобы тесты вызывали его напрямую с подставленными портами; порядок проверок —
    /// инвариант: ни одна ветка отказа не читает кэш, не запускает ffmpeg и не читает оригинал.
    /// </summary>
    /// <param name="httpContext">Контекст запроса (заголовки ответа).</param>
    /// <param name="assetId">Носитель из маршрута.</param>
    /// <param name="fileAccess">Резолвер носителя-источника (вид и режимные поля).</param>
    /// <param name="caseScope">Порт профиля: область дел субъекта (ТБ-071).</param>
    /// <param name="sessions">Поисковые сессии: кандидат взаимного поиска открывается на просмотр (ADR-0044).</param>
    /// <param name="storage">Файловое хранилище ядра — для временной копии, когда локального пути нет.</param>
    /// <param name="localFiles">Локальный путь к оригиналу (необязательный порт ядра).</param>
    /// <param name="extractor">Построение волны внешним процессом ffmpeg.</param>
    /// <param name="cache">Готовая волна в строке носителя.</param>
    /// <param name="accessProvider">Контекст доступа субъекта.</param>
    /// <param name="auditWriter">Неизменяемый журнал (ТБ-030).</param>
    /// <param name="viewAudit">Дроссель записей просмотра (одна на носитель в окне).</param>
    /// <param name="logger">Журнал диагностики (клиенту — всегда единый 404).</param>
    /// <param name="cancellationToken">Отмена (уход клиента останавливает ffmpeg).</param>
    public static async Task<IResult> ServeWaveformAsync(
        HttpContext httpContext,
        [FromRoute] int assetId,
        [FromServices] IMediaFileAccess fileAccess,
        [FromServices] ICaseScope caseScope,
        [FromServices] ISearchSessionStore sessions,
        [FromServices] IFileStorage storage,
        [FromServices] ILocalFileLocator? localFiles,
        [FromServices] IAudioWaveformExtractor extractor,
        [FromServices] IAudioWaveformCache cache,
        [FromServices] IAccessContextProvider accessProvider,
        [FromServices] IAuditWriter auditWriter,
        [FromServices] MediaViewAuditThrottle viewAudit,
        [FromServices] ILogger<MediaWaveformEndpointsCategory> logger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        // Волна — производная материала дела под решёткой: ни браузер, ни промежуточный кэш её не хранят (no-store).
        httpContext.Response.Headers.Append("X-Content-Type-Options", "nosniff");
        httpContext.Response.Headers.CacheControl = "private, no-store";

        var source = await fileAccess.ResolveFrameSourceAsync(assetId, cancellationToken);
        if (source is null)
        {
            MediaWaveformEndpointsLog.RejectedNotResolved(logger, assetId);
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
            MediaWaveformEndpointsLog.RejectedNoAccessContext(logger, assetId);
            return Results.NotFound();
        }

        if (!BaselineAccess.Filter<MediaFrameSource>(access).Compile()(source))
        {
            MediaWaveformEndpointsLog.RejectedOutsideClearance(
                logger, access.SubjectId, access.MaxClassification, source.Classification, source.DivisionId);
            return Results.NotFound();
        }

        // ПОВЕРХ floor — область дел субъекта (ТБ-071) либо кандидат его поиска (только просмотр, ADR-0044).
        if (!await AssetViewAccess.CanViewAsync(caseScope, sessions, assetId, access, cancellationToken))
        {
            MediaWaveformEndpointsLog.RejectedOutsideCases(logger, access.SubjectId, assetId);
            return Results.NotFound();
        }

        // Волна — только у аудиозаписи: у видео под проигрывателем лента кадров (ADR-0038), у фото звука нет.
        if (source.Kind != MediaKind.Audio)
        {
            MediaWaveformEndpointsLog.RejectedNotAudio(logger, assetId, source.Kind);
            return Results.NotFound();
        }

        var waveform = await cache.GetAsync(assetId, cancellationToken)
            ?? await BuildAsync(source, storage, localFiles, extractor, cache, logger, cancellationToken);
        if (waveform is null)
        {
            return Results.NotFound();
        }

        // Просмотр волны — аудируемое обращение к материалу (ТБ-030) с грифом и подразделением носителя; дроссель —
        // одна запись на носитель в окне, как у кадров и Range-продолжений записи.
        var objectRef = AuditObjectRefPrefix + assetId.ToString(CultureInfo.InvariantCulture);
        if (viewAudit.ShouldAudit(access.NumericSubjectId, objectRef))
        {
            await auditWriter.WriteAsync(
                new AuditEntry(
                    AuditAction.View, source.Classification, access.NumericSubjectId,
                    ObjectRef: objectRef,
                    DivisionId: source.DivisionId),
                cancellationToken);
        }

        return Results.Json(new AudioWaveformResponse(waveform.StepMs, waveform.DurationMs, Convert.ToBase64String(waveform.Peaks)));
    }

    /// <summary>
    /// Построить волну из оригинала и запомнить. <see langword="null"/> — построить не удалось (причина — в журнале
    /// диагностики); отмена вызывающим пробрасывается.
    /// </summary>
    private static async Task<AudioWaveform?> BuildAsync(
        MediaFrameSource source,
        IFileStorage storage,
        ILocalFileLocator? localFiles,
        IAudioWaveformExtractor extractor,
        IAudioWaveformCache cache,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var subPath = source.AssetId.ToString(CultureInfo.InvariantCulture);
        string? localPath;
        try
        {
            localPath = localFiles?.TryGetLocalPath(source.StoredFileName, MediaFileCategories.Originals, subPath);
        }
        catch (InvalidOperationException)
        {
            // Имя из БД вне корня хранилища — так не бывает (имена выдаёт само хранилище), но fail-closed дешевле веры.
            MediaWaveformEndpointsLog.RejectedBadStoredName(logger, source.AssetId);
            return null;
        }

        string? tempPath = null;
        try
        {
            var audioPath = localPath;
            if (audioPath is null)
            {
                try
                {
                    tempPath = await MediaFrameEndpoints.CopyToTempAsync(
                        storage, source.StoredFileName, subPath, TempFilePrefix, logger, cancellationToken);
                }
                catch (FileNotFoundException)
                {
                    MediaWaveformEndpointsLog.RejectedMissingOnDisk(logger, source.AssetId, source.StoredFileName);
                    return null;
                }

                audioPath = tempPath;
            }

            AudioWaveform waveform;
            try
            {
                waveform = await extractor.ExtractAsync(audioPath, cancellationToken);
            }
            catch (Exception exception) when (exception is InvalidOperationException or IOException
                                                  && !cancellationToken.IsCancellationRequested)
            {
                MediaWaveformEndpointsLog.BuildFailed(logger, exception, source.AssetId);
                return null;
            }

            await cache.SaveAsync(source.AssetId, waveform, cancellationToken);
            MediaWaveformEndpointsLog.Built(logger, source.AssetId, waveform.Peaks.Length, waveform.StepMs);
            return waveform;
        }
        finally
        {
            if (tempPath is not null)
            {
                MediaFrameEndpoints.TryDelete(tempPath, logger);
            }
        }
    }
}

/// <summary>Категория логгера эндпоинта волны (DI-якорь для <see cref="ILogger{TCategoryName}"/>).</summary>
public sealed class MediaWaveformEndpointsCategory;

/// <summary>Строго-типизированные лог-сообщения эндпоинта волны (LoggerMessage — CA1848); клиенту всегда единый 404.</summary>
internal static partial class MediaWaveformEndpointsLog
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Медиа: волна носителя {AssetId} — носителя нет")]
    public static partial void RejectedNotResolved(ILogger logger, int assetId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Медиа: волна носителя {AssetId} запрошена без контекста доступа")]
    public static partial void RejectedNoAccessContext(ILogger logger, int assetId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Медиа: субъект {SubjectId} (допуск {MaxClassification}) вне допуска к волне носителя (гриф {Classification}, подразделение {DivisionId})")]
    public static partial void RejectedOutsideClearance(ILogger logger, string subjectId, short maxClassification, short classification, int divisionId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Медиа: субъект {SubjectId} запросил волну носителя {AssetId} вне дел субъекта (ТБ-071)")]
    public static partial void RejectedOutsideCases(ILogger logger, string subjectId, int assetId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Медиа: волна носителя {AssetId} — носитель не аудио ({Kind})")]
    public static partial void RejectedNotAudio(ILogger logger, int assetId, MediaKind kind);

    [LoggerMessage(Level = LogLevel.Error, Message = "Медиа: волна носителя {AssetId} — имя оригинала в БД выходит за пределы хранилища")]
    public static partial void RejectedBadStoredName(ILogger logger, int assetId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Медиа: оригинал носителя {AssetId} ({StoredFileName}) есть в БД, но отсутствует на диске")]
    public static partial void RejectedMissingOnDisk(ILogger logger, int assetId, string storedFileName);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Медиа: волна носителя {AssetId} не построена")]
    public static partial void BuildFailed(ILogger logger, Exception exception, int assetId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Медиа: волна носителя {AssetId} построена: {PeakCount} пиков с шагом {StepMs} мс")]
    public static partial void Built(ILogger logger, int assetId, int peakCount, int stepMs);
}
