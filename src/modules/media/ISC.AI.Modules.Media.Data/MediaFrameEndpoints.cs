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

/// <summary>
/// Покадровый просмотр видео с сервера (ADR-0028, ТФ-МЕД-11): <c>GET /media/frames/{assetId}?t=&lt;мс&gt;</c>
/// отдаёт JPEG ОДНОГО кадра, вырезанного ffmpeg из ОРИГИНАЛА носителя, — для контейнеров, которые браузер не
/// воспроизводит (mkv/avi/mov/3gp). Проверки — те же и в том же порядке, что у раздачи файла носителя
/// (<see cref="MediaFileEndpoints"/>): аутентификация, floor ядра по строке носителя (ТБ-020/021), область дел
/// субъекта (ТБ-071), единый 404 на любой отказ, <c>nosniff</c>; плюс «только видео» и «момент не за концом записи».
/// </summary>
/// <remarks>
/// <para>
/// Оригинал читается НА МЕСТЕ через нейтральный порт ядра <see cref="ILocalFileLocator"/> (ТО-инф-10, ADR-0028 п.4):
/// копировать до 200 МБ на каждый шаг кадра нельзя ни по времени, ни по ТБ-064 (лишние копии материала дела).
/// Порт необязателен: хранилище без локальных путей его не регистрирует либо возвращает <see langword="null"/> —
/// тогда оригинал копируется во временный файл в управляемом каталоге <c>%TEMP%\iscai-media</c> и удаляется в
/// <c>finally</c>; остатки после аварийной остановки подбирает уборка при старте хоста (<c>MediaTempFiles</c> в
/// Application — имя каталога обязано совпадать с <see cref="TempFolderName"/>). Кэша кадров нет: кадр вырезается
/// по запросу (ДОК-13 §7).
/// </para>
/// <para>
/// Момент для ffmpeg: при известной нативной частоте кадров запрос переводится в номер кадра
/// (<see cref="VideoProbe.FrameIndexAt(long, double)"/>) и обратно в момент на полкадра раньше
/// (<see cref="VideoProbe.SeekTimeFor"/>) — так шаг «±1 кадр» в интерфейсе даёт ровно соседний кадр во всех
/// контейнерах; без частоты (носитель до пробы) момент берётся как есть.
/// </para>
/// </remarks>
public static class MediaFrameEndpoints
{
    /// <summary>Наибольшая сторона отдаваемого кадра, пиксели: 4K/1080p уменьшаются для просмотра, меньшие — как есть.</summary>
    public const int MaxSide = 1920;

    /// <summary>Тип ответа: кадр всегда JPEG независимо от контейнера оригинала.</summary>
    public const string FrameContentType = "image/jpeg";

    /// <summary>Каталог временных копий пакета внутри %TEMP% — тот же, что убирает <c>MediaTempFiles</c> при старте хоста.</summary>
    public const string TempFolderName = "iscai-media";

    /// <summary>Префикс временной копии оригинала для вырезки кадра (отличает её от копий других конвейеров).</summary>
    public const string TempFilePrefix = "frame-";

    /// <summary>Префикс <c>ObjectRef</c> записи журнала о просмотре кадров носителя: <c>media:frames:{assetId}</c>.</summary>
    public const string AuditObjectRefPrefix = "media:frames:";

    // Предел момента запроса: дальше TimeSpan непредставим (FromTicks переполнился бы). Реальные записи — часы.
    private const long MaxTimestampMs = long.MaxValue / TimeSpan.TicksPerMillisecond;

    /// <summary>
    /// Маршрут <see cref="MediaFileRoutes.FrameTemplate"/> (<c>GET /media/frames/{assetId}?t=&lt;мс&gt;</c>). Шаблон и имя
    /// параметра — из общего источника в Domain, тем же пользуется UI (<see cref="MediaFileRoutes.BuildFrame"/>).
    /// </summary>
    public static void MapMediaFrameEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Лимитер (MediaFrameRateLimitPolicy): каждый запрос — процесс ffmpeg, без предела один субъект исчерпал бы
        // CPU/память узла сотней параллельных GET. Отказ лимитера — 429 без тела: существование носителя не
        // раскрывается (ТБ-021), проверки доступа ниже к тому моменту ещё не выполнялись.
        endpoints.MapGet(MediaFileRoutes.FrameTemplate, ServeFrameAsync)
            .RequireAuthorization()
            .RequireRateLimiting(MediaFrameRateLimitPolicy.PolicyName)
            .WithName("MediaFrames");
    }

    /// <summary>
    /// Обработчик кадра. Публичный, чтобы тесты вызывали его напрямую с подставленными портами (у Minimal API нет
    /// иного шва); порядок проверок — инвариант: ни одна ветка отказа не запускает ffmpeg и не читает оригинал.
    /// </summary>
    /// <param name="httpContext">Контекст запроса (заголовки ответа).</param>
    /// <param name="assetId">Носитель из маршрута.</param>
    /// <param name="t">Момент записи, мс, из строки запроса (<see cref="MediaFileRoutes.FrameTimeQuery"/>); разбирается строго.</param>
    /// <param name="fileAccess">Резолвер носителя-источника.</param>
    /// <param name="caseScope">Порт профиля: область дел субъекта (ТБ-071).</param>
    /// <param name="storage">Файловое хранилище ядра — для временной копии, когда локального пути нет.</param>
    /// <param name="localFiles">Локальный путь к оригиналу (необязательный порт ядра, ADR-0028 п.4).</param>
    /// <param name="frames">Вырезка кадра внешним процессом ffmpeg.</param>
    /// <param name="accessProvider">Контекст доступа субъекта.</param>
    /// <param name="auditWriter">Неизменяемый журнал (ТБ-030).</param>
    /// <param name="viewAudit">Дроссель записей просмотра (одна на носитель в окне).</param>
    /// <param name="logger">Журнал диагностики (клиенту — всегда единый 404).</param>
    /// <param name="cancellationToken">Отмена (уход клиента останавливает ffmpeg).</param>
    public static async Task<IResult> ServeFrameAsync(
        HttpContext httpContext,
        [FromRoute] int assetId,
        [FromQuery(Name = MediaFileRoutes.FrameTimeQuery)] string? t,
        [FromServices] IMediaFileAccess fileAccess,
        [FromServices] ICaseScope caseScope,
        [FromServices] IFileStorage storage,
        [FromServices] ILocalFileLocator? localFiles,
        [FromServices] IFrameExtractor frames,
        [FromServices] IAccessContextProvider accessProvider,
        [FromServices] IAuditWriter auditWriter,
        [FromServices] MediaViewAuditThrottle viewAudit,
        [FromServices] ILogger<MediaFrameEndpointsCategory> logger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        // Кадр — материал дела под решёткой: ни браузер, ни промежуточный кэш не должны его хранить (no-store);
        // nosniff — как у раздачи файлов (MIME-confusion). Заголовки ставятся до любой ветки, в т.ч. для 404.
        httpContext.Response.Headers.Append("X-Content-Type-Options", "nosniff");
        httpContext.Response.Headers.CacheControl = "private, no-store";

        // Момент — только цифры (NumberStyles.None: без знака, пробелов и разделителей), не длиннее представимого.
        if (t is null
            || !long.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out var timestampMs)
            || timestampMs > MaxTimestampMs)
        {
            MediaFrameEndpointsLog.RejectedBadTime(logger, assetId, t);
            return Results.NotFound();
        }

        var source = await fileAccess.ResolveFrameSourceAsync(assetId, cancellationToken);
        if (source is null)
        {
            MediaFrameEndpointsLog.RejectedNotResolved(logger, assetId);
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
            MediaFrameEndpointsLog.RejectedNoAccessContext(logger, assetId);
            return Results.NotFound();
        }

        // Тот же floor, что у файлов и поиска (BaselineAccess): гриф ≤ допуск ∧ подразделение ∈ разрешённых.
        if (!BaselineAccess.Filter<MediaFrameSource>(access).Compile()(source))
        {
            MediaFrameEndpointsLog.RejectedOutsideClearance(
                logger, access.SubjectId, access.MaxClassification, source.Classification, source.DivisionId);
            return Results.NotFound();
        }

        // ПОВЕРХ floor — область дел субъекта (ТБ-071): иначе следователь того же подразделения перебором id
        // листал бы кадры чужих дел, а субъект без роли профиля — всё в допуске (default-deny, ТБ-012/021).
        if (!await caseScope.IsAssetAccessibleAsync(assetId, access, cancellationToken))
        {
            MediaFrameEndpointsLog.RejectedOutsideCases(logger, access.SubjectId, assetId);
            return Results.NotFound();
        }

        // Кадр есть только у видео: фото и аудио (в т.ч. «видео» без видеопотока, переведённое в аудио, ADR-0026)
        // этим маршрутом не отдаются — ffmpeg не запускается.
        if (source.Kind != MediaKind.Video)
        {
            MediaFrameEndpointsLog.RejectedNotVideo(logger, assetId, source.Kind);
            return Results.NotFound();
        }

        // Момент за концом записи — кадра нет; отсекается до ffmpeg ТОЛЬКО когда длительность точная, то есть от
        // пробы (частота известна). У носителя без пробы DurationMs — таймкод последнего кадра ВЫБОРКИ раскадровки
        // (1 к/с, округлён вниз до секунды): он отсёк бы хвост записи до ~1 с, поэтому там решает ffmpeg — за концом
        // он возвращает null, и ответ тот же 404.
        if (source.FrameRate is not null && source.DurationMs is { } durationMs && timestampMs > durationMs)
        {
            MediaFrameEndpointsLog.RejectedBeyondDuration(logger, assetId, timestampMs, durationMs);
            return Results.NotFound();
        }

        var subPath = assetId.ToString(CultureInfo.InvariantCulture);
        string? localPath;
        try
        {
            localPath = localFiles?.TryGetLocalPath(source.StoredFileName, MediaFileCategories.Originals, subPath);
        }
        catch (InvalidOperationException)
        {
            // Имя из БД вне корня хранилища — так не бывает (имена выдаёт само хранилище), но fail-closed дешевле веры.
            MediaFrameEndpointsLog.RejectedBadStoredName(logger, assetId);
            return Results.NotFound();
        }

        var at = SeekTimeFor(timestampMs, source.FrameRate);

        string? tempPath = null;
        try
        {
            var videoPath = localPath;
            if (videoPath is null)
            {
                try
                {
                    tempPath = await CopyToTempAsync(storage, source.StoredFileName, subPath, logger, cancellationToken);
                }
                catch (FileNotFoundException)
                {
                    MediaFrameEndpointsLog.RejectedMissingOnDisk(logger, assetId, source.StoredFileName);
                    return Results.NotFound();
                }

                videoPath = tempPath;
            }

            var jpeg = await frames.ExtractFrameAsync(videoPath, at, FrameImageFormat.Jpeg, MaxSide, cancellationToken);
            if (jpeg is null)
            {
                MediaFrameEndpointsLog.RejectedNoFrame(logger, assetId, timestampMs);
                return Results.NotFound();
            }

            // Просмотр кадров носителя — аудируемое событие (ТБ-030/072) с грифом и подразделением носителя. Ключ
            // дросселя — НОСИТЕЛЬ, а не (носитель, t): покадровое листание — это сотни запросов в минуту с новым t
            // в каждом; запись на каждый кадр раздула бы журнал впустую (смысл у всех один — «субъект листал
            // кадры носителя сейчас»), а словарь дросселя добирал бы порог чистки (10 000 ключей) за минуты
            // листания. Одна запись на носитель в окне — та же семантика, что у Range-продолжений видео
            // (MediaViewAuditThrottle); допуск при этом проверен для КАЖДОГО запроса выше.
            var objectRef = AuditObjectRefPrefix + subPath;
            if (viewAudit.ShouldAudit(access.NumericSubjectId, objectRef))
            {
                await auditWriter.WriteAsync(
                    new AuditEntry(
                        AuditAction.View, source.Classification, access.NumericSubjectId,
                        ObjectRef: objectRef,
                        DivisionId: source.DivisionId),
                    cancellationToken);
            }

            return Results.File(jpeg, FrameContentType);
        }
        finally
        {
            if (tempPath is not null)
            {
                TryDelete(tempPath, logger);
            }
        }
    }

    /// <summary>
    /// Момент для ffmpeg по запрошенному <paramref name="timestampMs"/>: при известной частоте — начало кадра № N
    /// на полкадра раньше (<see cref="VideoProbe.SeekTimeFor"/>), иначе — момент как есть.
    /// </summary>
    public static TimeSpan SeekTimeFor(long timestampMs, double? frameRate)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(timestampMs);
        if (frameRate is { } fps && fps > 0)
        {
            return VideoProbe.SeekTimeFor(VideoProbe.FrameIndexAt(timestampMs, fps), fps);
        }

        return TimeSpan.FromTicks(timestampMs * TimeSpan.TicksPerMillisecond);
    }

    // Временная копия оригинала для хранилища без локальных путей. Источник открывается ПЕРВЫМ: файла нет на
    // диске — исключение до того, как во временном каталоге появится хоть что-то. Имя — префикс, GUID и
    // БЕЗОПАСНОЕ расширение (MediaFileNames): путь уходит ffmpeg текстом командной строки.
    private static async Task<string> CopyToTempAsync(
        IFileStorage storage, string storedFileName, string subPath, ILogger logger, CancellationToken cancellationToken)
    {
        await using var original = await storage.OpenReadAsync(
            storedFileName, MediaFileCategories.Originals, subPath, cancellationToken);

        var directory = Path.Combine(Path.GetTempPath(), TempFolderName);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(
            directory, TempFilePrefix + Guid.NewGuid().ToString("N") + MediaFileNames.SafeExtension(storedFileName));
        try
        {
            await using var copy = new FileStream(
                path, FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize: 81920, useAsync: true);
            await original.CopyToAsync(copy, cancellationToken);
        }
        catch
        {
            TryDelete(path, logger); // недописанная копия материала дела не должна остаться (ТБ-064)
            throw;
        }

        return path;
    }

    // Удаление временной копии; неудача пишется в журнал с путём — файл снимет уборка при старте либо оператор.
    private static void TryDelete(string path, ILogger logger)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException exception)
        {
            MediaFrameEndpointsLog.TempCopyNotRemoved(logger, exception, path);
        }
        catch (UnauthorizedAccessException exception)
        {
            MediaFrameEndpointsLog.TempCopyNotRemoved(logger, exception, path);
        }
    }
}

/// <summary>Категория логгера эндпоинта кадра (DI-якорь для <see cref="ILogger{TCategoryName}"/>).</summary>
public sealed class MediaFrameEndpointsCategory;

/// <summary>Строго-типизированные лог-сообщения эндпоинта кадра (LoggerMessage — CA1848); клиенту всегда единый 404.</summary>
internal static partial class MediaFrameEndpointsLog
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "Медиа: кадр носителя {AssetId} — неверный момент «{Time}»")]
    public static partial void RejectedBadTime(ILogger logger, int assetId, string? time);

    [LoggerMessage(Level = LogLevel.Information, Message = "Медиа: кадр носителя {AssetId} — носителя нет")]
    public static partial void RejectedNotResolved(ILogger logger, int assetId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Медиа: кадр носителя {AssetId} запрошен без контекста доступа")]
    public static partial void RejectedNoAccessContext(ILogger logger, int assetId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Медиа: субъект {SubjectId} (допуск {MaxClassification}) вне допуска к кадрам носителя (гриф {Classification}, подразделение {DivisionId})")]
    public static partial void RejectedOutsideClearance(ILogger logger, string subjectId, short maxClassification, short classification, int divisionId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Медиа: субъект {SubjectId} запросил кадр носителя {AssetId} вне дел субъекта (ТБ-071)")]
    public static partial void RejectedOutsideCases(ILogger logger, string subjectId, int assetId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Медиа: кадр носителя {AssetId} — носитель не видео ({Kind})")]
    public static partial void RejectedNotVideo(ILogger logger, int assetId, MediaKind kind);

    [LoggerMessage(Level = LogLevel.Information, Message = "Медиа: кадр носителя {AssetId} — момент {TimestampMs} мс за концом записи ({DurationMs} мс)")]
    public static partial void RejectedBeyondDuration(ILogger logger, int assetId, long timestampMs, long durationMs);

    [LoggerMessage(Level = LogLevel.Error, Message = "Медиа: кадр носителя {AssetId} — имя оригинала в БД выходит за пределы хранилища")]
    public static partial void RejectedBadStoredName(ILogger logger, int assetId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Медиа: оригинал носителя {AssetId} ({StoredFileName}) есть в БД, но отсутствует на диске")]
    public static partial void RejectedMissingOnDisk(ILogger logger, int assetId, string storedFileName);

    [LoggerMessage(Level = LogLevel.Information, Message = "Медиа: кадр носителя {AssetId} в момент {TimestampMs} мс не вырезан (за концом записи)")]
    public static partial void RejectedNoFrame(ILogger logger, int assetId, long timestampMs);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Медиа: временная копия для вырезки кадра не удалена: {Path}")]
    public static partial void TempCopyNotRemoved(ILogger logger, Exception exception, string path);
}
