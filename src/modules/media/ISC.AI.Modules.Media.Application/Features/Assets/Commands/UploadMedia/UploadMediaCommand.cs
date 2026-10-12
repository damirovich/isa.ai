using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.BackgroundTasks;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.Media.Domain.Model;
using ISC.AI.Modules.Media.Domain.Services;
using Mediator;
using Microsoft.Extensions.DependencyInjection;

namespace ISC.AI.Modules.Media.Application.Features.Assets;

/// <summary>
/// Загрузить носитель (фото/видео/аудио) в дело (ТФ-МЕД-01, ТС-010; аудио — ADR-0026): приём с дедупликацией
/// по хешу, привязка к делу по значению (ТО-инф-08) и постановка фоновой обработки в очередь (ТП-005):
/// индексации лиц — для фото и видео, расшифровки речи — для аудио и видео.
/// </summary>
/// <remarks>
/// ГРИФ И ПОДРАЗДЕЛЕНИЕ НОСИТЕЛЯ БЕРУТСЯ У ДЕЛА (ТБ-070, ТБ-024): пользователь их не выбирает и «по
/// умолчанию» они не подставляются — недоступное дело означает отказ ещё до приёма байтов. Дубликат
/// (тот же SHA-256 в том же подразделении) к делу привязывается, но повторно не обрабатывается.
/// Семейство файла (изображение/видео/аудио) сверяется с СОДЕРЖИМЫМ (<see cref="ContentSniffer"/>), а не с
/// заявленным типом: подмена типа отклоняется до приёма остальных байтов. Индексация лиц к аудио НЕ ставится вовсе:
/// лиц в нём нет, и конвейер биометрии не должен касаться материала, к которому он неприменим (ADR-0026).
/// ПРИЁМ ПОТОКОМ (ADR-0040): файл не держится в памяти — читается заголовок для проверки сигнатуры, затем поток
/// целиком уходит во временный файл хранилища. Предел — по виду носителя (<see cref="MediaUploadOptions"/>), и
/// проверяется по ФАКТИЧЕСКИ прочитанному объёму, а не по заявленной длине.
/// </remarks>
public sealed record UploadMediaCommand(
    int CaseId,
    string FileName,
    string ContentType,
    MediaUploadContent Content,
    string? Source = null,
    DateTimeOffset? CapturedAt = null,
    string? Place = null)
    : IRequest<ResponseDto<MediaAssetReceipt>>, IAuditableRequest
{
    /// <summary>Вид фоновой задачи индексации лиц (показывается в списке задач).</summary>
    public const string IndexingTaskKind = "Индексация носителя (распознавание лиц)";

    /// <summary>Вид фоновой задачи расшифровки речи (ADR-0026).</summary>
    public const string TranscriptionTaskKind = "Расшифровка речи носителя";

    /// <summary>Сколько байтов начала файла читается для проверки сигнатуры (с запасом над <see cref="ContentSniffer.SignatureLength"/>).</summary>
    public const int HeaderBytes = 4096;

    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.Ingest;

    /// <inheritdoc />
    public string? AuditSummary => $"media:upload:case={CaseId};file={FileName}";

    /// <inheritdoc cref="UploadMediaCommand" />
    public sealed class Handler(
        IMediaAdministration administration,
        IAccessContextProvider accessProvider,
        ICaseScope caseScope,
        IMediaStore store,
        IBackgroundTaskQueue queue,
        MediaUploadOptions? uploadOptions = null)
        : IRequestHandler<UploadMediaCommand, ResponseDto<MediaAssetReceipt>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<MediaAssetReceipt>> Handle(
            UploadMediaCommand command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);

            if (!await administration.CanUploadAsync(cancellationToken))
            {
                return ResponseDto<MediaAssetReceipt>.BadRequest("Загрузка носителей в дело доступна ролям Следователь и Администратор.");
            }

            // Fail-closed (ТБ-020/021): дело вне допуска/роли неотличимо от несуществующего.
            var access = await accessProvider.GetCurrentAsync(cancellationToken);
            var caseItem = await caseScope.GetCaseAsync(command.CaseId, access, cancellationToken);
            if (caseItem is null)
            {
                return ResponseDto<MediaAssetReceipt>.NotFound("Дело не найдено или недоступно.");
            }

            var kind = MediaFileRules.KindOf(command.ContentType);
            if (kind is null)
            {
                return ResponseDto<MediaAssetReceipt>.BadRequest("Формат файла не поддерживается.");
            }

            // Предел — по виду носителя (ADR-0040): заявленная длина отсекает заведомо большой файл до чтения,
            // фактическая проверяется при чтении (UploadContentStream).
            var maxBytes = (uploadOptions ?? MediaUploadOptions.Default).MaxBytesFor(kind.Value);
            if (command.Content.Length > maxBytes)
            {
                return ResponseDto<MediaAssetReceipt>.BadRequest(TooLargeMessage(maxBytes));
            }

            await using var source = command.Content.TakeStream();

            // ТС-010: семейство — по сигнатуре байтов, не со слов клиента; расхождение или неизвестная сигнатура — отказ.
            // Читается только начало файла; остальное пойдёт в хранилище вслед за ним, без второго чтения.
            // Универсальные контейнеры (m4a/3gp/webm) допускаются и как аудио — см. ContentSniffer.IsCompatible.
            var header = new byte[HeaderBytes];
            var headerLength = await source.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false, cancellationToken);
            if (ContentSniffer.Sniff(header.AsSpan(0, headerLength)) is null)
            {
                return ResponseDto<MediaAssetReceipt>.BadRequest(
                    "Содержимое файла не распознано как изображение, видео или аудио поддерживаемого формата.");
            }

            if (!ContentSniffer.IsCompatible(header.AsSpan(0, headerLength), kind.Value))
            {
                return ResponseDto<MediaAssetReceipt>.BadRequest("Содержимое файла не соответствует заявленному типу.");
            }

            var draft = new MediaAssetDraft(
                command.FileName,
                command.ContentType,
                kind.Value,
                caseItem.Classification,
                caseItem.DivisionId,
                command.Source,
                command.CapturedAt,
                access.NumericSubjectId);

            MediaAssetReceipt receipt;
            try
            {
                await using var content = new UploadContentStream(header, headerLength, source, maxBytes);
                receipt = await store.ReceiveAsync(draft, content, cancellationToken);
            }
            catch (MediaUploadTooLargeException)
            {
                // Источник заявил меньше, чем прислал: временный файл хранилище уже удалило, в деле ничего нет.
                return ResponseDto<MediaAssetReceipt>.BadRequest(TooLargeMessage(maxBytes));
            }

            await caseScope.LinkAssetAsync(
                command.CaseId, receipt.AssetId, command.Place, access.NumericSubjectId, cancellationToken);

            if (!receipt.Duplicate)
            {
                // Захватываем только примитив: scope текущего запроса к моменту выполнения уже закрыт.
                var assetId = receipt.AssetId;

                // Лица — только там, где они могут быть (фото, видео). К аудио конвейер биометрии не
                // запускается вовсе: статус носителя уже «неприменимо» (ADR-0026).
                if (kind is MediaKind.Image or MediaKind.Video)
                {
                    await queue.EnqueueAsync(
                        IndexingTaskKind,
                        async (sp, ct) => await sp.GetRequiredService<IMediaIndexer>().IndexAsync(assetId, ct),
                        cancellationToken);
                }

                // Речь — аудио и звуковая дорожка видео (ADR-0026). Отдельная задача: сбой одного конвейера
                // не мешает другому, и у каждого свой статус в карточке.
                if (kind is MediaKind.Audio or MediaKind.Video)
                {
                    await queue.EnqueueAsync(
                        TranscriptionTaskKind,
                        async (sp, ct) => await sp.GetRequiredService<IMediaTranscriptionPipeline>().TranscribeAsync(assetId, ct),
                        cancellationToken);
                }
            }

            return ResponseDto<MediaAssetReceipt>.Ok(receipt);
        }

        private static string TooLargeMessage(long maxBytes) =>
            $"Файл больше допустимых {MediaFileRules.DescribeSize(maxBytes)}.";
    }
}
