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
    public const string IndexingTaskKind = MediaProcessing.IndexingTaskKind;

    /// <summary>Вид фоновой задачи расшифровки речи (ADR-0026).</summary>
    public const string TranscriptionTaskKind = MediaProcessing.TranscriptionTaskKind;

    /// <summary>Сколько байтов начала файла читается для проверки сигнатуры (с запасом над <see cref="ContentSniffer.SignatureLength"/>).</summary>
    public const int HeaderBytes = MediaIntake.SignatureHeaderBytes;

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

            // Общий приём (ADR-0040/0041): формат, предел по виду, сигнатура содержимого, хеш, дедупликация, привязка
            // к делу. Гриф и подразделение — дела (ТБ-070).
            var intake = new MediaIntake(store, caseScope, uploadOptions);
            var result = await intake.ReceiveAsync(
                new MediaIntakeRequest(
                    caseItem,
                    command.FileName,
                    command.ContentType,
                    command.Content.Length,
                    command.Source,
                    command.CapturedAt,
                    command.Place,
                    access.NumericSubjectId),
                command.Content.TakeStream(),
                cancellationToken);
            if (result.Receipt is not { } receipt)
            {
                return ResponseDto<MediaAssetReceipt>.BadRequest(result.Message ?? MediaIntake.UnsupportedFormatMessage);
            }

            // Дубликат к делу привязан, но повторно не обрабатывается.
            if (!receipt.Duplicate)
            {
                await MediaProcessing.EnqueueAsync(queue, receipt.AssetId, result.Kind!.Value, cancellationToken);
            }

            return ResponseDto<MediaAssetReceipt>.Ok(receipt);
        }
    }
}
