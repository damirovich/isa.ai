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
/// Загрузить носитель (фото/видео) в дело (ТФ-МЕД-01, ТС-010): приём с дедупликацией по хешу, привязка к
/// делу по значению (ТО-инф-08) и постановка индексации в фоновую очередь (ТП-005).
/// </summary>
/// <remarks>
/// ГРИФ И ПОДРАЗДЕЛЕНИЕ НОСИТЕЛЯ БЕРУТСЯ У ДЕЛА (ТБ-070, ТБ-024): пользователь их не выбирает и «по
/// умолчанию» они не подставляются — недоступное дело означает отказ ещё до приёма байтов. Дубликат
/// (тот же SHA-256 в том же подразделении) к делу привязывается, но повторно не индексируется.
/// </remarks>
public sealed record UploadMediaCommand(
    int CaseId,
    string FileName,
    string ContentType,
    byte[] Content,
    string? Source = null,
    DateTimeOffset? CapturedAt = null,
    string? Place = null)
    : IRequest<ResponseDto<MediaAssetReceipt>>, IAuditableRequest
{
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
        IBackgroundTaskQueue queue)
        : IRequestHandler<UploadMediaCommand, ResponseDto<MediaAssetReceipt>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<MediaAssetReceipt>> Handle(
            UploadMediaCommand command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);

            if (!await administration.CanUploadAsync(cancellationToken))
            {
                return ResponseDto<MediaAssetReceipt>.BadRequest("Загрузка носителей доступна ролям Следователь, Руководитель, Администратор.");
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
            using (var content = new MemoryStream(command.Content))
            {
                receipt = await store.ReceiveAsync(draft, content, cancellationToken);
            }

            await caseScope.LinkAssetAsync(
                command.CaseId, receipt.AssetId, command.Place, access.NumericSubjectId, cancellationToken);

            if (!receipt.Duplicate)
            {
                // Захватываем только примитив: scope текущего запроса к моменту выполнения уже закрыт.
                var assetId = receipt.AssetId;
                await queue.EnqueueAsync(
                    "Индексация носителя (распознавание лиц)",
                    async (sp, ct) => await sp.GetRequiredService<IMediaIndexer>().IndexAsync(assetId, ct),
                    cancellationToken);
            }

            return ResponseDto<MediaAssetReceipt>.Ok(receipt);
        }
    }
}
