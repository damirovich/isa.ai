using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentValidation;
using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using ISC.AI.Abstractions.Storage;
using ISC.AI.Modules.Media.Domain.Model;
using ISC.AI.Modules.Media.Domain.Services;
using Mediator;

namespace ISC.AI.Modules.Media.Application.Features.Print;

/// <summary>Снимок фототаблицы: носитель-изображение, подпись, цвета текста и подложки, наносить ли пометки (ADR-0050).</summary>
/// <param name="AssetId">Носитель-изображение своего дела.</param>
/// <param name="Caption">Подпись (псевдоним, дата, место); пустая — без полосы.</param>
/// <param name="TextColor">Цвет текста <c>#RRGGBB</c>.</param>
/// <param name="BackgroundColor">Цвет подложки <c>#RRGGBB</c>.</param>
/// <param name="WithAnnotations">Нанести пометки носителя на копию для листа.</param>
public sealed record PhotoSheetEntry(
    int AssetId, string? Caption, string TextColor = "#000000", string BackgroundColor = "#FFFFFF", bool WithAnnotations = false);

/// <summary>Сформированная фототаблица: имя файла, PDF, гриф (максимум по снимкам) и число листов.</summary>
/// <param name="FileName">Имя файла.</param>
/// <param name="Content">PDF.</param>
/// <param name="Classification">Гриф листа.</param>
/// <param name="Pages">Листов.</param>
public sealed record PhotoSheetFile(string FileName, byte[] Content, short Classification, int Pages);

/// <summary>
/// Печать фото — фототаблица в PDF (ТФ-МЕД-20, ADR-0057): выбранные снимки дела раскладываются на листы A4 по
/// <paramref name="PerPage"/>, под каждым — подпись; на каждой странице — гриф (максимум по снимкам), «Выдал» и водяной
/// знак получателя (ТБ-033, ТБ-076). Оригиналы не меняются (ТЭ-007): лист формируется при выдаче и не хранится.
/// </summary>
/// <remarks>
/// ИНВАРИАНТ (ТБ-071, ТБ-021): печатаются только носители-изображения СВОИХ дел субъекта (как скачивание оригинала);
/// если недоступен хотя бы один — отказ всей выгрузки одним ответом, неотличимым от «нет такого». Без определённого
/// сотрудника — отказ: «кто выдал» на листе обязателен. Запись журнала <see cref="AuditAction.Export"/> — номера
/// носителей и раскладка, без подписей (ТБ-032: в них ФИО и псевдонимы).
/// </remarks>
/// <param name="Entries">Снимки по порядку на листе.</param>
/// <param name="PerPage">Снимков на лист: 1, 2, 4, 6, 9, 12, 16, 20.</param>
/// <param name="SingleWidthMm">Ширина снимка при одном на лист; вместе с высотой — «произвольный размер».</param>
/// <param name="SingleHeightMm">Высота снимка при одном на лист.</param>
/// <param name="Title">Заголовок листа; пусто — «Фототаблица».</param>
public sealed record ExportPhotoSheetQuery(
    IReadOnlyList<PhotoSheetEntry> Entries,
    int PerPage,
    double? SingleWidthMm = null,
    double? SingleHeightMm = null,
    string? Title = null)
    : IRequest<ResponseDto<PhotoSheetFile>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.Export;

    /// <inheritdoc />
    public string? AuditSummary => string.Create(
        CultureInfo.InvariantCulture,
        $"media:photo-sheet:export;assets={string.Join(',', (Entries ?? []).Select(e => e.AssetId))};per-page={PerPage};annotated={(Entries ?? []).Count(e => e.WithAnnotations)}");

    /// <inheritdoc cref="ExportPhotoSheetQuery" />
    public sealed class Handler(
        IAccessContextProvider accessProvider,
        IMediaCatalog catalog,
        ICaseScope caseScope,
        IMediaAnnotationStore annotations,
        IFileStorage fileStorage,
        IImageTools imageTools,
        ISubjectProvider subjectProvider,
        IUserAccountStore accounts,
        TimeProvider time) : IRequestHandler<ExportPhotoSheetQuery, ResponseDto<PhotoSheetFile>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<PhotoSheetFile>> Handle(ExportPhotoSheetQuery query, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(query);

            var access = await accessProvider.GetCurrentAsync(cancellationToken);

            // Сначала — доступ ко ВСЕМ снимкам: лист из части снимков не формируется (ТБ-071, ТБ-021).
            var assets = new Dictionary<int, MediaAssetRow>();
            foreach (var id in query.Entries.Select(e => e.AssetId).Distinct())
            {
                var asset = await catalog.GetAsync(id, access, cancellationToken);
                if (asset is null || asset.Kind != MediaKind.Image || !await caseScope.IsAssetAccessibleAsync(asset.Id, access, cancellationToken))
                {
                    return ResponseDto<PhotoSheetFile>.NotFound(PhotoSheetMessages.NotFound);
                }

                assets[id] = asset;
            }

            if (await subjectProvider.GetCurrentUserIdAsync(cancellationToken) is not { } userId)
            {
                return ResponseDto<PhotoSheetFile>.BadRequest(PhotoSheetMessages.NoExecutor);
            }

            var account = (await accounts.ListAsync(cancellationToken)).FirstOrDefault(a => a.UserId == userId);
            var executor = account is null
                ? string.Create(CultureInfo.InvariantCulture, $"пользователь № {userId}")
                : string.IsNullOrWhiteSpace(account.DisplayName) ? account.UserName : account.DisplayName;

            PhotoSheetSize? single = query.SingleWidthMm is { } width && query.SingleHeightMm is { } height
                ? new PhotoSheetSize(width, height)
                : null;
            var pages = PhotoSheetLayout.Pages(query.Entries.Count, query.PerPage, single);
            var boxes = pages.SelectMany(p => p.Cells).ToDictionary(c => c.Index, c => c.Image);

            var photos = new List<PhotoSheetPhoto>(query.Entries.Count);
            try
            {
                for (var index = 0; index < query.Entries.Count; index++)
                {
                    var entry = query.Entries[index];
                    var asset = assets[entry.AssetId];
                    var image = await ReadOriginalAsync(asset, cancellationToken);
                    if (entry.WithAnnotations && await annotations.ListAsync(asset.Id, access, cancellationToken) is { Count: > 0 } marks)
                    {
                        image = imageTools.RenderAnnotated(image, marks, caption: null);
                    }

                    // Ужатие сразу после чтения: оригинал (до 200 МБ) не держится в памяти до конца листа.
                    var box = boxes[index];
                    image = imageTools.FitJpeg(image, PhotoSheetLayout.PixelsAt300Dpi(box.Width), PhotoSheetLayout.PixelsAt300Dpi(box.Height));
                    photos.Add(new PhotoSheetPhoto(image, Clean(entry.Caption), entry.TextColor, entry.BackgroundColor));
                }
            }
            catch (InvalidOperationException)
            {
                return ResponseDto<PhotoSheetFile>.BadRequest(PhotoSheetMessages.Unreadable);
            }

            // Гриф листа — наибольший из грифов снимков (ТБ-033, шкала ADR-0030).
            var classification = ClassificationLevels.Normalize(assets.Values.Max(a => a.Classification));
            var issuedAt = time.GetLocalNow().DateTime;
            var document = new PhotoSheetDocument(
                photos,
                query.PerPage,
                single,
                Clean(query.Title) ?? PhotoSheetLayout.DefaultTitle,
                ClassificationLevels.Marking(classification),
                executor,
                issuedAt);

            byte[] pdf;
            try
            {
                pdf = imageTools.RenderPhotoSheetPdf(document);
            }
            catch (InvalidOperationException)
            {
                return ResponseDto<PhotoSheetFile>.BadRequest(PhotoSheetMessages.Unreadable);
            }

            var fileName = string.Create(CultureInfo.InvariantCulture, $"{document.Title} {issuedAt:dd.MM.yyyy HH-mm}.pdf");
            return ResponseDto<PhotoSheetFile>.Ok(new PhotoSheetFile(SafeFileName(fileName), pdf, classification, pages.Count));
        }

        private async Task<byte[]> ReadOriginalAsync(MediaAssetRow asset, CancellationToken cancellationToken)
        {
            await using var source = await fileStorage.OpenReadAsync(
                asset.StoredFileName, MediaFileCategories.Originals, asset.Id.ToString(CultureInfo.InvariantCulture), cancellationToken);
            using var buffer = new MemoryStream();
            await source.CopyToAsync(buffer, cancellationToken);
            return buffer.ToArray();
        }

        private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

        // Заголовок вводит сотрудник — знаки, недопустимые в имени файла, заменяются.
        private static string SafeFileName(string name) =>
            string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c is '/' or '\\' or ':' ? '_' : c));
    }
}

/// <summary>
/// Правила фототаблицы: от 1 до <see cref="PhotoSheetLayout.MaxPhotos"/> снимков, число на лист из перечня, цвета
/// <c>#RRGGBB</c>, длины подписей и заголовка; «произвольный размер» — обе стороны, только при одном снимке на лист и в
/// пределах печатаемой области листа.
/// </summary>
public sealed class ExportPhotoSheetValidator : AbstractValidator<ExportPhotoSheetQuery>
{
    /// <inheritdoc cref="ExportPhotoSheetValidator" />
    public ExportPhotoSheetValidator()
    {
        RuleFor(q => q.Entries)
            .NotEmpty().WithMessage("Выберите хотя бы один снимок.")
            .Must(entries => entries is null || entries.Count <= PhotoSheetLayout.MaxPhotos)
            .WithMessage($"За раз — не больше {PhotoSheetLayout.MaxPhotos} снимков.");
        RuleForEach(q => q.Entries).ChildRules(entry =>
        {
            entry.RuleFor(e => e.AssetId).GreaterThan(0);
            entry.RuleFor(e => e.Caption).MaximumLength(PhotoSheetLayout.MaxCaptionLength);
            entry.RuleFor(e => e.TextColor).Must(PhotoSheetLayout.IsColor).WithMessage("Цвет текста — в виде #RRGGBB.");
            entry.RuleFor(e => e.BackgroundColor).Must(PhotoSheetLayout.IsColor).WithMessage("Цвет подложки — в виде #RRGGBB.");
        });
        RuleFor(q => q.PerPage)
            .Must(perPage => PhotoSheetLayout.PerPageOptions.Contains(perPage))
            .WithMessage("Снимков на лист: 1, 2, 4, 6, 9, 12, 16 или 20.");
        RuleFor(q => q.Title).MaximumLength(PhotoSheetLayout.MaxTitleLength);
        RuleFor(q => q)
            .Must(q => (q.SingleWidthMm is null) == (q.SingleHeightMm is null))
            .WithMessage("Произвольный размер задаётся шириной и высотой вместе.")
            .Must(q => q.SingleWidthMm is null || q.PerPage == 1)
            .WithMessage("Произвольный размер — только при одном снимке на лист.")
            .Must(q => q.SingleWidthMm is not { } width || q.SingleHeightMm is not { } height
                || PhotoSheetLayout.FitsSingle(new PhotoSheetSize(width, height)))
            .WithMessage(string.Create(
                CultureInfo.InvariantCulture,
                $"Размер снимка — от {PhotoSheetLayout.MinSingleMm:0} мм до {PhotoSheetLayout.MaxSingleWidthMm:0} мм по ширине и {PhotoSheetLayout.MaxSingleHeightMm:0} мм по высоте."));
    }
}

/// <summary>Снимок, выбранный для печати: что показать на странице «Печать фото».</summary>
/// <param name="AssetId">Носитель.</param>
/// <param name="FileName">Имя файла.</param>
/// <param name="StoredFileName">Имя в хранилище — для просмотра оригинала через эндпоинт с проверкой доступа.</param>
/// <param name="CapturedAt">Время съёмки.</param>
/// <param name="Classification">Гриф носителя.</param>
/// <param name="AnnotationCount">Пометок на снимке (ADR-0050).</param>
/// <param name="Origin">Происхождение: «фото», «снимок кадра», «обработанная копия».</param>
public sealed record PrintableAsset(
    int AssetId, string FileName, string StoredFileName, DateTimeOffset? CapturedAt, short Classification, int AnnotationCount, string Origin);

/// <summary>
/// Снимки для страницы «Печать фото» (ADR-0057): из названных номеров — только фото своих дел субъекта (ТБ-071);
/// остальные молча не показываются — недоступное неотличимо от несуществующего (ТБ-021). Одна запись журнала на
/// открытие страницы.
/// </summary>
/// <param name="AssetIds">Носители по порядку выбора.</param>
public sealed record ListPrintableAssetsQuery(IReadOnlyList<int> AssetIds)
    : IRequest<ResponseDto<IReadOnlyList<PrintableAsset>>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.View;

    /// <inheritdoc />
    public string? AuditSummary => string.Create(
        CultureInfo.InvariantCulture, $"media:photo-sheet:view;assets={string.Join(',', AssetIds ?? [])}");

    /// <inheritdoc cref="ListPrintableAssetsQuery" />
    public sealed class Handler(
        IAccessContextProvider accessProvider, IMediaCatalog catalog, ICaseScope caseScope, IMediaAnnotationStore annotations)
        : IRequestHandler<ListPrintableAssetsQuery, ResponseDto<IReadOnlyList<PrintableAsset>>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<IReadOnlyList<PrintableAsset>>> Handle(
            ListPrintableAssetsQuery query, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(query);

            var access = await accessProvider.GetCurrentAsync(cancellationToken);
            var rows = new List<PrintableAsset>();
            foreach (var id in query.AssetIds.Distinct())
            {
                if (await catalog.GetAsync(id, access, cancellationToken) is not { Kind: MediaKind.Image } asset
                    || !await caseScope.IsAssetAccessibleAsync(asset.Id, access, cancellationToken))
                {
                    continue;
                }

                var marks = await annotations.ListAsync(asset.Id, access, cancellationToken);
                var origin = asset.IsEditedCopy ? "обработанная копия" : asset.SourceAssetId is not null ? "снимок кадра" : "фото";
                rows.Add(new PrintableAsset(asset.Id, asset.OriginalFileName, asset.StoredFileName, asset.CapturedAt, asset.Classification,
                    marks.Count, origin));
            }

            return ResponseDto<IReadOnlyList<PrintableAsset>>.Ok(rows, rows.Count);
        }
    }
}

/// <summary>Правила списка снимков для печати: от 1 до <see cref="PhotoSheetLayout.MaxPhotos"/> номеров.</summary>
public sealed class ListPrintableAssetsValidator : AbstractValidator<ListPrintableAssetsQuery>
{
    /// <inheritdoc cref="ListPrintableAssetsValidator" />
    public ListPrintableAssetsValidator()
    {
        RuleFor(q => q.AssetIds)
            .NotEmpty()
            .Must(ids => ids is null || ids.Count <= PhotoSheetLayout.MaxPhotos)
            .WithMessage($"За раз — не больше {PhotoSheetLayout.MaxPhotos} снимков.");
        RuleForEach(q => q.AssetIds).GreaterThan(0);
    }
}

/// <summary>Тексты фототаблицы.</summary>
public static class PhotoSheetMessages
{
    /// <summary>Единый ответ «нет или нельзя» (ТБ-021): хотя бы один снимок недоступен или не изображение.</summary>
    public const string NotFound = "Снимки не найдены либо недоступны: печатаются только фотографии ваших дел.";

    /// <summary>Сотрудник не определён — «кто выдал» на листе обязателен.</summary>
    public const string NoExecutor = "Не удалось определить сотрудника: фототаблица без подписи «Выдал» не формируется.";

    /// <summary>Снимок не распознан.</summary>
    public const string Unreadable = "Снимок не распознан: формат не поддерживается или файл повреждён.";
}
