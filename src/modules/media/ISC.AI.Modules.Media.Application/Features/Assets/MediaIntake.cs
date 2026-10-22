using ISC.AI.Abstractions.BackgroundTasks;
using ISC.AI.Modules.Media.Domain.Model;
using ISC.AI.Modules.Media.Domain.Services;
using Microsoft.Extensions.DependencyInjection;

namespace ISC.AI.Modules.Media.Application.Features.Assets;

/// <summary>Почему файл не принят общим приёмом (<see cref="MediaIntake"/>).</summary>
public enum MediaIntakeFailure
{
    /// <summary>Формат (MIME-тип) не из allowlist.</summary>
    UnsupportedFormat = 1,

    /// <summary>Файл больше предела своего вида (ADR-0040) — по заявленной или по фактической длине.</summary>
    TooLarge = 2,

    /// <summary>Содержимое не распознано как изображение, видео или аудио (ТС-010).</summary>
    UnrecognizedContent = 3,

    /// <summary>Семейство содержимого не соответствует заявленному типу (ТС-010).</summary>
    TypeMismatch = 4,

    /// <summary>У фото или видео нет даты съёмки ни в описи, ни в метаданных (ТФ-МЕД-17).</summary>
    MissingCaptureTime = 5,

    /// <summary>Контрольная сумма не совпала с описью (ADR-0041) — файл в хранилище не записан.</summary>
    ChecksumMismatch = 6,
}

/// <summary>Что принять: файл и реквизиты носителя для дела.</summary>
/// <param name="Case">Дело, уже проверенное вызывающим на доступность субъекту: гриф и подразделение носителя — его (ТБ-070).</param>
/// <param name="FileName">Исходное имя файла.</param>
/// <param name="ContentType">Заявленный MIME-тип (семейство сверяется с содержимым).</param>
/// <param name="DeclaredLength">Заявленная длина, байт (ей не доверяют — считается фактический объём).</param>
/// <param name="Source">Источник (свободный текст).</param>
/// <param name="CapturedAt">Дата съёмки от оператора или из описи.</param>
/// <param name="Place">Место (на привязке к делу).</param>
/// <param name="UploaderUserId">Кто загрузил.</param>
/// <param name="ExpectedContentHash">SHA-256 из описи импорта — сверяется до записи в хранилище.</param>
/// <param name="ImportItemId">Строка задания импорта (происхождение носителя, ADR-0041).</param>
/// <param name="CaptureTimeZone">
/// Пояс для даты съёмки из метаданных без смещения. Задан — дата съёмки, если её нет в <paramref name="CapturedAt"/>,
/// читается из начала файла (EXIF, заголовок MP4/MOV, <see cref="CaptureTimeProbe"/>) и у фото и видео ОБЯЗАТЕЛЬНА
/// (ТФ-МЕД-17): так принимает импорт архива, где оператор не подтверждает дату каждого файла глазами. Не задан —
/// дата берётся только из <paramref name="CapturedAt"/>, обязательность проверяет валидатор команды загрузки.
/// </param>
public sealed record MediaIntakeRequest(
    CaseScopeItem Case,
    string FileName,
    string ContentType,
    long DeclaredLength,
    string? Source = null,
    DateTimeOffset? CapturedAt = null,
    string? Place = null,
    int? UploaderUserId = null,
    string? ExpectedContentHash = null,
    int? ImportItemId = null,
    TimeZoneInfo? CaptureTimeZone = null);

/// <summary>Итог общего приёма.</summary>
/// <param name="Receipt">Носитель (новый или дубликат); <see langword="null"/> — отказ.</param>
/// <param name="Kind">Вид носителя.</param>
/// <param name="CapturedAt">Дата съёмки, с которой носитель принят.</param>
/// <param name="Failure">Почему не принят.</param>
/// <param name="Message">Текст отказа для человека.</param>
public sealed record MediaIntakeResult(
    MediaAssetReceipt? Receipt,
    MediaKind? Kind,
    DateTimeOffset? CapturedAt,
    MediaIntakeFailure? Failure,
    string? Message)
{
    /// <summary>Файл принят (новым носителем или дубликатом).</summary>
    public bool Accepted => Receipt is not null;

    internal static MediaIntakeResult Fail(MediaIntakeFailure failure, string message, MediaKind? kind = null) =>
        new(null, kind, null, failure, message);
}

/// <summary>
/// Общий приём файла носителя в дело (ТФ-МЕД-01, ТС-010, ADR-0040/0041) — один путь для загрузки из браузера и для
/// импорта архива: формат из allowlist, предел по виду, сигнатура содержимого по заголовку, SHA-256 по ходу записи,
/// дедупликация (ТФ-МЕД-04), привязка к делу. Права и доступность дела проверяет вызывающий ДО приёма.
/// </summary>
/// <remarks>
/// Обработку (индексацию лиц, расшифровку) приём НЕ ставит: загрузка ставит её сразу (<see cref="MediaProcessing"/>),
/// импорт архива — малыми порциями, чтобы новые материалы не ждали архив (ADR-0041 п. 7).
/// </remarks>
public sealed class MediaIntake(IMediaStore store, ICaseScope caseScope, MediaUploadOptions? uploadOptions = null)
{
    /// <summary>Сколько байтов начала файла читается для проверки сигнатуры (с запасом над <see cref="ContentSniffer.SignatureLength"/>).</summary>
    public const int SignatureHeaderBytes = 4096;

    /// <summary>Отказ: формат не из allowlist.</summary>
    public const string UnsupportedFormatMessage = "Формат файла не поддерживается.";

    /// <summary>Отказ: содержимое не распознано.</summary>
    public const string UnrecognizedContentMessage =
        "Содержимое файла не распознано как изображение, видео или аудио поддерживаемого формата.";

    /// <summary>Отказ: содержимое не соответствует типу.</summary>
    public const string TypeMismatchMessage = "Содержимое файла не соответствует заявленному типу.";

    /// <summary>Отказ: нет даты съёмки у фото или видео при импорте.</summary>
    public const string MissingCaptureTimeMessage =
        "Нет даты съёмки: в описи она не указана, в метаданных файла не найдена — укажите её в описи (ТФ-МЕД-17).";

    /// <summary>Отказ по пределу размера.</summary>
    public static string TooLargeMessage(long maxBytes) => $"Файл больше допустимых {MediaFileRules.DescribeSize(maxBytes)}.";

    /// <summary>
    /// Принимает файл. Поток забирается: приём читает его один раз и закрывает (в том числе при отказе).
    /// </summary>
    /// <param name="request">Реквизиты.</param>
    /// <param name="content">Содержимое (не обязательно перематываемое).</param>
    /// <param name="cancellationToken">Отмена: прерывает приём, временный файл удаляется хранилищем.</param>
    public async Task<MediaIntakeResult> ReceiveAsync(
        MediaIntakeRequest request, Stream content, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(content);

        await using var source = content;

        var kind = MediaFileRules.KindOf(request.ContentType);
        if (kind is null)
        {
            return MediaIntakeResult.Fail(MediaIntakeFailure.UnsupportedFormat, UnsupportedFormatMessage);
        }

        // Предел — по виду носителя (ADR-0040): заявленная длина отсекает заведомо большой файл до чтения,
        // фактическая проверяется при чтении (UploadContentStream).
        var maxBytes = (uploadOptions ?? MediaUploadOptions.Default).MaxBytesFor(kind.Value);
        if (request.DeclaredLength > maxBytes)
        {
            return MediaIntakeResult.Fail(MediaIntakeFailure.TooLarge, TooLargeMessage(maxBytes), kind);
        }

        // ТС-010: семейство — по сигнатуре байтов, не со слов источника. Читается только начало файла (у импорта —
        // больше, чтобы найти дату съёмки в метаданных); остальное идёт в хранилище вслед за ним без второго чтения.
        var readCaptureTime = request.CaptureTimeZone is not null && request.CapturedAt is null;
        var header = new byte[readCaptureTime ? Math.Max(SignatureHeaderBytes, CaptureTimeProbe.HeadBytes) : SignatureHeaderBytes];
        var headerLength = await source.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false, cancellationToken);
        var head = header.AsSpan(0, headerLength);
        if (ContentSniffer.Sniff(head) is null)
        {
            return MediaIntakeResult.Fail(MediaIntakeFailure.UnrecognizedContent, UnrecognizedContentMessage, kind);
        }

        if (!ContentSniffer.IsCompatible(head, kind.Value))
        {
            return MediaIntakeResult.Fail(MediaIntakeFailure.TypeMismatch, TypeMismatchMessage, kind);
        }

        var capturedAt = request.CapturedAt;
        if (readCaptureTime)
        {
            // ТФ-МЕД-17 при импорте: значение из метаданных — то же, что подсказывает форма загрузки; подтверждение
            // оператором — галочка запуска задания (ADR-0041 п. 4). Без даты фото и видео не принимаются.
            capturedAt = CaptureTimeProbe.TryRead(head, request.CaptureTimeZone!, DateTimeOffset.UtcNow);
            if (capturedAt is null && kind is MediaKind.Image or MediaKind.Video)
            {
                return MediaIntakeResult.Fail(MediaIntakeFailure.MissingCaptureTime, MissingCaptureTimeMessage, kind);
            }
        }

        var draft = new MediaAssetDraft(
            request.FileName,
            request.ContentType,
            kind.Value,
            request.Case.Classification,
            request.Case.DivisionId,
            request.Source,
            capturedAt,
            request.UploaderUserId,
            ExpectedContentHash: request.ExpectedContentHash,
            ImportItemId: request.ImportItemId);

        MediaAssetReceipt receipt;
        try
        {
            await using var stream = new UploadContentStream(header, headerLength, source, maxBytes);
            receipt = await store.ReceiveAsync(draft, stream, cancellationToken);
        }
        catch (MediaUploadTooLargeException)
        {
            // Источник заявил меньше, чем прислал: временный файл хранилище уже удалило, в деле ничего нет.
            return MediaIntakeResult.Fail(MediaIntakeFailure.TooLarge, TooLargeMessage(maxBytes), kind);
        }
        catch (MediaContentHashMismatchException mismatch)
        {
            // Файл повреждён при переносе или опись ошибочна: в хранилище он не попал (ADR-0041 п. 6).
            return MediaIntakeResult.Fail(MediaIntakeFailure.ChecksumMismatch, mismatch.Message, kind);
        }

        await caseScope.LinkAssetAsync(
            request.Case.CaseId, receipt.AssetId, request.Place, request.UploaderUserId, cancellationToken);

        return new MediaIntakeResult(receipt, kind, capturedAt, null, null);
    }
}

/// <summary>Задачи обработки, поставленные для носителя.</summary>
/// <param name="IndexTaskId">Индексация лиц (фото, видео).</param>
/// <param name="TranscriptTaskId">Расшифровка речи (аудио, видео).</param>
public sealed record MediaProcessingTasks(Guid? IndexTaskId, Guid? TranscriptTaskId);

/// <summary>
/// Постановка обработки нового носителя в общую фоновую очередь (ТП-005, ADR-0026): индексация лиц — для фото и
/// видео, расшифровка речи — для аудио и видео. Одно место для загрузки и для импорта архива.
/// </summary>
public static class MediaProcessing
{
    /// <summary>Вид фоновой задачи индексации лиц (показывается в списке задач).</summary>
    public const string IndexingTaskKind = "Индексация носителя (распознавание лиц)";

    /// <summary>Вид фоновой задачи расшифровки речи (ADR-0026).</summary>
    public const string TranscriptionTaskKind = "Расшифровка речи носителя";

    /// <summary>Ставит конвейеры, применимые к виду носителя.</summary>
    /// <param name="queue">Общая фоновая очередь.</param>
    /// <param name="assetId">Носитель.</param>
    /// <param name="kind">Вид носителя.</param>
    /// <param name="cancellationToken">Отмена постановки.</param>
    public static async Task<MediaProcessingTasks> EnqueueAsync(
        IBackgroundTaskQueue queue, int assetId, MediaKind kind, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(queue);

        // Лица — только там, где они могут быть (фото, видео). К аудио конвейер биометрии не запускается вовсе:
        // статус носителя уже «неприменимо» (ADR-0026). Захватывается только примитив: scope постановки к моменту
        // выполнения уже закрыт.
        Guid? index = null;
        if (kind is MediaKind.Image or MediaKind.Video)
        {
            index = await queue.EnqueueAsync(
                IndexingTaskKind,
                async (sp, ct) => await sp.GetRequiredService<IMediaIndexer>().IndexAsync(assetId, ct),
                cancellationToken);
        }

        // Речь — аудио и звуковая дорожка видео (ADR-0026). Отдельная задача: сбой одного конвейера не мешает
        // другому, и у каждого свой статус в карточке.
        Guid? transcript = null;
        if (kind is MediaKind.Audio or MediaKind.Video)
        {
            transcript = await queue.EnqueueAsync(
                TranscriptionTaskKind,
                async (sp, ct) => await sp.GetRequiredService<IMediaTranscriptionPipeline>().TranscribeAsync(assetId, cancellationToken: ct),
                cancellationToken);
        }

        return new MediaProcessingTasks(index, transcript);
    }
}
