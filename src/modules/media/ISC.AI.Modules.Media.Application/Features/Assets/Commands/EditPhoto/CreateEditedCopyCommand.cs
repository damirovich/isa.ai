using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using ISC.AI.Abstractions.Storage;
using ISC.AI.Modules.Media.Domain.Model;
using ISC.AI.Modules.Media.Domain.Services;
using Mediator;

namespace ISC.AI.Modules.Media.Application.Features.Assets.Commands.EditPhoto;

/// <summary>Итог сохранения обработанной копии.</summary>
/// <param name="AssetId">Новый носитель-копия (или уже существующий с теми же байтами).</param>
/// <param name="Duplicate">Такая копия уже была — новый носитель не создан.</param>
/// <param name="Operations">Перечень операций для человека.</param>
public sealed record EditedCopyResult(int AssetId, bool Duplicate, string Operations);

/// <summary>
/// Обработанная копия фотографии (ТФ-МЕД-14, ADR-0050): из ОРИГИНАЛА по набору операций строится новая картинка
/// (PNG без потерь) и принимается в то же дело отдельным носителем — со ссылкой на источник, перечнем операций, грифом и
/// подразделением источника. Оригинал не меняется никогда (ТЭ-007).
/// </summary>
/// <remarks>
/// <para>ПОРЯДОК ПРОВЕРОК — как у снимка кадра (ADR-0028): право → решётка на строке носителя (ТБ-020) → сужение по
/// делам субъекта (ТБ-071) → вид «фото» → не копия (копию обрабатывают от источника, цепочек копий нет) → набор не пуст →
/// дело источника. Недоступный носитель неотличим от несуществующего (ТБ-021).</para>
/// <para>КОПИЯ НЕ ИНДЕКСИРУЕТСЯ (хранилище ставит «неприменимо», переиндексация её отклоняет): шаблоны с обработанного
/// изображения были бы вторыми шаблонами того же человека, а копия — частью эталонной базы. Уничтожается вместе с
/// источником (ТНД-008, <c>MediaPurger</c>). Тот же набор над тем же оригиналом даёт те же байты — хранилище вернёт
/// существующую копию (ТНД-002).</para>
/// <para>ЖУРНАЛ (ТБ-030): запись поведения («кто и что запросил») и запись <see cref="AuditAction.Ingest"/> на новый
/// носитель с грифом источника: откуда и какие операции применены (ТЭ-007 — «применённые обработки перечисляются»).</para>
/// </remarks>
/// <param name="AssetId">Носитель-источник (фото, не копия).</param>
/// <param name="Edit">Набор операций.</param>
public sealed record CreateEditedCopyCommand(int AssetId, ImageEdit Edit) : IRequest<ResponseDto<EditedCopyResult>>, IAuditableRequest
{
    /// <summary>Отказ: носитель вне допуска, вне дел субъекта или не существует (ТБ-020/021).</summary>
    public const string NotFoundMessage = "Носитель не найден или недоступен.";

    /// <summary>Отказ: обрабатывать можно только фотографии.</summary>
    public const string NotImageMessage = "Обработка доступна только для фотографий.";

    /// <summary>Отказ: носитель — уже обработанная копия.</summary>
    public const string AlreadyCopyMessage = "Это обработанная копия: обрабатывайте исходный носитель.";

    /// <summary>Отказ: не выбрано ни одной операции.</summary>
    public const string IdentityMessage = "Не выбрано ни одной обработки — копия совпала бы с оригиналом.";

    /// <summary>Отказ: у носителя нет дела, доступного субъекту.</summary>
    public const string NotLinkedMessage = "Носитель не привязан к делу: копию сохранить некуда.";

    /// <summary>Отказ: право обработки — как у загрузки.</summary>
    public const string NoRightMessage = "Обработка фотографий доступна ролям Следователь и Администратор.";

    /// <summary>Наибольший обрабатываемый оригинал, байт (фото крупнее в памяти не обрабатывается).</summary>
    public const long MaxSourceBytes = 200L * 1024 * 1024;

    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.Ingest;

    /// <inheritdoc />
    public string? AuditSummary =>
        "media:edited-copy:source=" + AssetId.ToString(CultureInfo.InvariantCulture) + ";ops=" + (Edit?.Describe() ?? "-");

    /// <inheritdoc cref="CreateEditedCopyCommand" />
    public sealed class Handler(
        IMediaAdministration administration,
        IAccessContextProvider accessProvider,
        IMediaCatalog catalog,
        ICaseScope caseScope,
        IMediaStore store,
        IFileStorage fileStorage,
        IImageTools imageTools,
        IAuditWriter auditWriter) : IRequestHandler<CreateEditedCopyCommand, ResponseDto<EditedCopyResult>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<EditedCopyResult>> Handle(CreateEditedCopyCommand command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);
            ArgumentNullException.ThrowIfNull(command.Edit);

            if (!await administration.CanUploadAsync(cancellationToken))
            {
                return ResponseDto<EditedCopyResult>.BadRequest(NoRightMessage);
            }

            // Fail-closed (ТБ-020/021) и сужение по делам субъекта (ТБ-071): чужой носитель по перебираемому
            // идентификатору не обработать; отказ неотличим от «не найден».
            var access = await accessProvider.GetCurrentAsync(cancellationToken);
            var asset = await catalog.GetAsync(command.AssetId, access, cancellationToken);
            if (asset is null || !await caseScope.IsAssetAccessibleAsync(asset.Id, access, cancellationToken))
            {
                return ResponseDto<EditedCopyResult>.NotFound(NotFoundMessage);
            }

            if (asset.Kind != MediaKind.Image)
            {
                return ResponseDto<EditedCopyResult>.BadRequest(NotImageMessage);
            }

            if (asset.IsEditedCopy)
            {
                return ResponseDto<EditedCopyResult>.BadRequest(AlreadyCopyMessage);
            }

            if (command.Edit.IsIdentity)
            {
                return ResponseDto<EditedCopyResult>.BadRequest(IdentityMessage);
            }

            if (asset.ByteSize > MaxSourceBytes)
            {
                return ResponseDto<EditedCopyResult>.BadRequest("Фотография слишком велика для обработки на сервере.");
            }

            var caseId = await caseScope.GetCaseIdForAssetAsync(asset.Id, access, cancellationToken);
            if (caseId is null)
            {
                return ResponseDto<EditedCopyResult>.BadRequest(NotLinkedMessage);
            }

            byte[] original;
            await using (var source = await fileStorage.OpenReadAsync(
                asset.StoredFileName, MediaFileCategories.Originals, asset.Id.ToString(CultureInfo.InvariantCulture), cancellationToken))
            using (var buffer = new MemoryStream())
            {
                await source.CopyToAsync(buffer, cancellationToken);
                original = buffer.ToArray();
            }

            byte[] png;
            try
            {
                png = imageTools.ApplyEdits(original, command.Edit);
            }
            catch (ArgumentException exception)
            {
                // Набор недопустим для этого изображения (кадрирование за краем, результат слишком велик) — причина как есть.
                return ResponseDto<EditedCopyResult>.BadRequest(exception.Message.Split(" (Parameter", 2)[0]);
            }
            catch (InvalidOperationException)
            {
                return ResponseDto<EditedCopyResult>.BadRequest("Изображение не распознано: формат не поддерживается или файл повреждён.");
            }

            var operations = command.Edit.Describe();
            var number = asset.Id.ToString(CultureInfo.InvariantCulture);
            var draft = new MediaAssetDraft(
                EditedCopyNames.FileName(asset.OriginalFileName),
                "image/png",
                MediaKind.Image,
                asset.Classification,
                asset.DivisionId,
                Source: "Обработанная копия носителя № " + number + ": " + operations,
                CapturedAt: asset.CapturedAt,
                UploadedByUserId: access.NumericSubjectId,
                SourceAssetId: asset.Id,
                EditOperations: command.Edit.ToJson());

            MediaAssetReceipt receipt;
            using (var content = new MemoryStream(png))
            {
                receipt = await store.ReceiveAsync(draft, content, cancellationToken);
            }

            await caseScope.LinkAssetAsync(caseId.Value, receipt.AssetId, place: null, access.NumericSubjectId, cancellationToken);

            await auditWriter.WriteAsync(
                new AuditEntry(
                    AuditAction.Ingest,
                    asset.Classification,
                    access.NumericSubjectId,
                    ObjectRef: "media:asset:" + receipt.AssetId.ToString(CultureInfo.InvariantCulture) + ":edited-copy",
                    DivisionId: asset.DivisionId,
                    PayloadSensitive: "обработанная копия носителя № " + number + "; операции: " + operations
                        + "; оригинал не изменён (ТЭ-007), копия не индексируется (ТФ-МЕД-14)"),
                cancellationToken);

            return ResponseDto<EditedCopyResult>.Ok(
                new EditedCopyResult(receipt.AssetId, receipt.Duplicate, operations),
                receipt.Duplicate
                    ? "Такая копия уже есть: носитель № " + receipt.AssetId.ToString(CultureInfo.InvariantCulture) + "."
                    : "Обработанная копия сохранена как носитель № " + receipt.AssetId.ToString(CultureInfo.InvariantCulture) + ".");
        }
    }
}

/// <summary>Имена обработанной копии.</summary>
public static class EditedCopyNames
{
    /// <summary>Имя файла копии: «снимок.jpg» → «снимок (обработка).png».</summary>
    public static string FileName(string originalFileName)
    {
        var name = Path.GetFileNameWithoutExtension(originalFileName);
        return (string.IsNullOrWhiteSpace(name) ? "фото" : name) + " (обработка).png";
    }
}
