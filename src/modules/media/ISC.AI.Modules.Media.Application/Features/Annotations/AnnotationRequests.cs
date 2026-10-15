using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using ISC.AI.Abstractions.Storage;
using ISC.AI.Modules.Media.Domain.Model;
using ISC.AI.Modules.Media.Domain.Services;
using Mediator;

namespace ISC.AI.Modules.Media.Application.Features.Annotations;

/// <summary>Слой пометок фото для карточки: пометки по порядку и имена их авторов.</summary>
/// <param name="Items">Пометки.</param>
/// <param name="Authors">Имена авторов по идентификатору сотрудника.</param>
public sealed record AnnotationLayer(IReadOnlyList<AnnotationRow> Items, IReadOnlyDictionary<int, string> Authors);

/// <summary>
/// Пометки фотографии для карточки (ТФ-МЕД-13, ADR-0050) — вместе с именами авторов. Видны и материалу чужого дела,
/// открытому как кандидат поиска (только просмотр, ADR-0044): пометки — часть того, что смотрят.
/// </summary>
/// <remarks>
/// НЕ <see cref="IAuditableRequest"/> — намеренно, как расшифровка: пометки показываются только в карточке, а открытие
/// карточки уже записано в журнал (ТБ-030); перечитывание после постановки пометки журнал не засоряет. Решётка — по
/// строке пометки на стороне БД, поверх неё — область дел субъекта либо кандидат его поиска (ТБ-071).
/// </remarks>
/// <param name="AssetId">Фотография.</param>
public sealed record ListAnnotationsQuery(int AssetId) : IRequest<ResponseDto<AnnotationLayer>>
{
    /// <inheritdoc cref="ListAnnotationsQuery" />
    public sealed class Handler(
        IAccessContextProvider accessProvider,
        ICaseScope caseScope,
        ISearchSessionStore sessions,
        IMediaAnnotationStore annotations,
        IUserAccountStore accounts) : IRequestHandler<ListAnnotationsQuery, ResponseDto<AnnotationLayer>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<AnnotationLayer>> Handle(ListAnnotationsQuery query, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(query);

            var access = await accessProvider.GetCurrentAsync(cancellationToken);
            if (!await AssetViewAccess.CanViewAsync(caseScope, sessions, query.AssetId, access, cancellationToken))
            {
                return ResponseDto<AnnotationLayer>.NotFound(AnnotationMessages.NotFound);
            }

            var items = await annotations.ListAsync(query.AssetId, access, cancellationToken);
            var authorIds = items.Where(i => i.AuthorUserId is not null).Select(i => i.AuthorUserId!.Value).ToHashSet();
            var authors = authorIds.Count == 0
                ? new Dictionary<int, string>()
                : (await accounts.ListAsync(cancellationToken))
                    .Where(a => authorIds.Contains(a.UserId))
                    .ToDictionary(a => a.UserId, a => string.IsNullOrWhiteSpace(a.DisplayName) ? a.UserName : a.DisplayName);
            return ResponseDto<AnnotationLayer>.Ok(new AnnotationLayer(items, authors), items.Count);
        }
    }
}

/// <summary>
/// Поставить пометку на фотографию (ТФ-МЕД-13, ADR-0050): стрелку, рамку или подпись — слоем поверх оригинала (файл не
/// меняется, ТЭ-007), с грифом и подразделением носителя.
/// </summary>
/// <remarks>
/// Право — как у загрузки (Следователь, Администратор); только носитель СВОЕГО дела (ТБ-071) — материал чужого дела,
/// открытый как кандидат поиска (ADR-0044), только просматривается. Автор и время — в строке и в журнале (ТБ-030,
/// <see cref="AuditAction.Modify"/>); текст подписи в сводку журнала не идёт (ТБ-032).
/// </remarks>
/// <param name="Draft">Пометка.</param>
public sealed record AddAnnotationCommand(AnnotationDraft Draft) : IRequest<ResponseDto<int>>, IAuditableRequest
{
    /// <summary>Отказ: право пометок — как у загрузки.</summary>
    public const string NoRightMessage = "Пометки на фотографиях ставят роли Следователь и Администратор.";

    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.Modify;

    /// <inheritdoc />
    public string? AuditSummary =>
        $"media:asset:{Draft?.AssetId.ToString(CultureInfo.InvariantCulture)}:annotation:add;kind={Draft?.Kind}";

    /// <inheritdoc cref="AddAnnotationCommand" />
    public sealed class Handler(
        IMediaAdministration administration,
        IAccessContextProvider accessProvider,
        ICaseScope caseScope,
        IMediaAnnotationStore annotations) : IRequestHandler<AddAnnotationCommand, ResponseDto<int>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<int>> Handle(AddAnnotationCommand command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);
            ArgumentNullException.ThrowIfNull(command.Draft);

            if (!await administration.CanUploadAsync(cancellationToken))
            {
                return ResponseDto<int>.BadRequest(NoRightMessage);
            }

            var errors = command.Draft.Validate();
            if (errors.Count > 0)
            {
                return ResponseDto<int>.BadRequest(string.Join(" ", errors));
            }

            var access = await accessProvider.GetCurrentAsync(cancellationToken);
            if (!await caseScope.IsAssetAccessibleAsync(command.Draft.AssetId, access, cancellationToken))
            {
                return ResponseDto<int>.NotFound(AnnotationMessages.NotFound);
            }

            var id = await annotations.AddAsync(command.Draft, access.NumericSubjectId, access, cancellationToken);
            return id is { } created
                ? ResponseDto<int>.Ok(created)
                : ResponseDto<int>.NotFound(AnnotationMessages.NotFound);
        }
    }
}

/// <summary>Снять пометку (ТФ-МЕД-13, ADR-0050); снятие — запись журнала, как и постановка.</summary>
/// <param name="AnnotationId">Пометка.</param>
public sealed record DeleteAnnotationCommand(int AnnotationId) : IRequest<ResponseDto<bool>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.Modify;

    /// <inheritdoc />
    public string? AuditSummary => $"media:annotation:{AnnotationId.ToString(CultureInfo.InvariantCulture)}:delete";

    /// <inheritdoc cref="DeleteAnnotationCommand" />
    public sealed class Handler(
        IMediaAdministration administration,
        IAccessContextProvider accessProvider,
        ICaseScope caseScope,
        IMediaAnnotationStore annotations) : IRequestHandler<DeleteAnnotationCommand, ResponseDto<bool>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<bool>> Handle(DeleteAnnotationCommand command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);

            if (!await administration.CanUploadAsync(cancellationToken))
            {
                return ResponseDto<bool>.BadRequest(AddAnnotationCommand.NoRightMessage);
            }

            var access = await accessProvider.GetCurrentAsync(cancellationToken);
            if (await annotations.GetAssetIdAsync(command.AnnotationId, access, cancellationToken) is not { } assetId
                || !await caseScope.IsAssetAccessibleAsync(assetId, access, cancellationToken)
                || !await annotations.DeleteAsync(command.AnnotationId, access, cancellationToken))
            {
                return ResponseDto<bool>.NotFound(AnnotationMessages.NotFound);
            }

            return ResponseDto<bool>.Ok(true);
        }
    }
}

/// <summary>Фото с нанесёнными пометками для выгрузки: имя файла и байты PNG.</summary>
/// <param name="FileName">Имя файла.</param>
/// <param name="Content">PNG.</param>
public sealed record AnnotatedImage(string FileName, byte[] Content);

/// <summary>
/// Выгрузка фотографии с пометками (ТФ-МЕД-13, ADR-0050: «включаются в экспорт по выбору»): оригинал с нанесёнными
/// пометками и полосой внизу — гриф носителя (ТБ-033), носитель, число пометок, кто и когда выгрузил. Оригинал не
/// меняется — это новая картинка только для выгрузки, в хранилище она не сохраняется.
/// </summary>
/// <remarks>
/// Только носитель своего дела (ТБ-071): материал чужого дела (кандидат поиска, ADR-0044) выгрузить нельзя, как и
/// скачать оригинал. Запись журнала <see cref="AuditAction.Export"/> (гриф — не ниже допуска субъекта). Без определённого
/// сотрудника — отказ: «кто выгрузил» в подписи обязателен.
/// </remarks>
/// <param name="AssetId">Фотография.</param>
public sealed record ExportAnnotatedImageQuery(int AssetId) : IRequest<ResponseDto<AnnotatedImage>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.Export;

    /// <inheritdoc />
    public string? AuditSummary => $"media:asset:{AssetId.ToString(CultureInfo.InvariantCulture)}:annotated:export;format=png";

    /// <inheritdoc cref="ExportAnnotatedImageQuery" />
    public sealed class Handler(
        IAccessContextProvider accessProvider,
        IMediaCatalog catalog,
        ICaseScope caseScope,
        IMediaAnnotationStore annotations,
        IFileStorage fileStorage,
        IImageTools imageTools,
        ISubjectProvider subjectProvider,
        IUserAccountStore accounts,
        TimeProvider time) : IRequestHandler<ExportAnnotatedImageQuery, ResponseDto<AnnotatedImage>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<AnnotatedImage>> Handle(ExportAnnotatedImageQuery query, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(query);

            var access = await accessProvider.GetCurrentAsync(cancellationToken);
            var asset = await catalog.GetAsync(query.AssetId, access, cancellationToken);
            if (asset is null || asset.Kind != MediaKind.Image || !await caseScope.IsAssetAccessibleAsync(asset.Id, access, cancellationToken))
            {
                return ResponseDto<AnnotatedImage>.NotFound(AnnotationMessages.NotFound);
            }

            if (await subjectProvider.GetCurrentUserIdAsync(cancellationToken) is not { } userId)
            {
                return ResponseDto<AnnotatedImage>.BadRequest("Не удалось определить сотрудника: выгрузка без подписи «кто выгрузил» не выполняется.");
            }

            var marks = await annotations.ListAsync(asset.Id, access, cancellationToken);
            var account = (await accounts.ListAsync(cancellationToken)).FirstOrDefault(a => a.UserId == userId);
            var executor = account is null
                ? string.Create(CultureInfo.InvariantCulture, $"пользователь № {userId}")
                : string.IsNullOrWhiteSpace(account.DisplayName) ? account.UserName : account.DisplayName;
            var caption = AnnotationMessages.Caption(asset, marks.Count, executor, time.GetLocalNow().DateTime);

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
                png = imageTools.RenderAnnotated(original, marks, caption);
            }
            catch (InvalidOperationException)
            {
                return ResponseDto<AnnotatedImage>.BadRequest("Изображение не распознано: формат не поддерживается или файл повреждён.");
            }

            var name = Path.GetFileNameWithoutExtension(asset.OriginalFileName);
            return ResponseDto<AnnotatedImage>.Ok(new AnnotatedImage((string.IsNullOrWhiteSpace(name) ? "фото" : name) + " (с пометками).png", png));
        }
    }
}

/// <summary>Тексты пометок.</summary>
public static class AnnotationMessages
{
    /// <summary>Единый ответ «нет или нельзя» (ТБ-021).</summary>
    public const string NotFound = "Носитель или пометка не найдены либо недоступны.";

    /// <summary>
    /// Подпись выгружаемого фото (ТБ-033): маркировка грифа, носитель, число пометок, кто и когда выгрузил.
    /// </summary>
    public static string Caption(MediaAssetRow asset, int annotationCount, string executor, DateTime exportedAt)
    {
        ArgumentNullException.ThrowIfNull(asset);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{ClassificationLevels.Marking(asset.Classification)} · носитель № {asset.Id} «{asset.OriginalFileName}» · пометок: {annotationCount} · {executor} · {exportedAt:dd.MM.yyyy HH:mm}");
    }
}
