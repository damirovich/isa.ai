using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Security;
using ISC.AI.Abstractions.Storage;
using ISC.AI.Modules.Media.Application.Features.Print;
using ISC.AI.Modules.Media.Domain.Model;
using ISC.AI.Modules.Media.Domain.Services;
using ISC.AI.Vision.Onnx.Imaging;
using NSubstitute;
using Shouldly;
using SkiaSharp;
using UglyToad.PdfPig;
using Xunit;

namespace ISC.AI.UnitTests.Media;

/// <summary>
/// Печать фото — фототаблица (ТФ-МЕД-20, ADR-0057): раскладка листа A4 (сетки 1–20, листы, «произвольный размер»,
/// вписывание с пропорциями); настоящий Skia — ужатие снимка без увеличения, пометки без полосы подписи, PDF с нужным
/// числом листов, маркировкой грифа, «Выдал» и свойствами файла; выгрузка — только фото своих дел, гриф листа —
/// максимум, без сотрудника — отказ, журнал без подписей; список для страницы печати и правила формы.
/// </summary>
public sealed class PhotoSheetTests
{
    private static readonly AccessContext Access = new("42", 3, [7]);

    [Theory(DisplayName = "Сетка: 1, 2, 4, 6, 9, 12, 16, 20 снимков на лист — столбцы × строки; другое число — отказ")]
    [InlineData(1, 1, 1)]
    [InlineData(2, 1, 2)]
    [InlineData(4, 2, 2)]
    [InlineData(6, 2, 3)]
    [InlineData(9, 3, 3)]
    [InlineData(12, 3, 4)]
    [InlineData(16, 4, 4)]
    [InlineData(20, 4, 5)]
    public void Grid_matches_per_page(int perPage, int columns, int rows)
    {
        PhotoSheetLayout.Grid(perPage).ShouldBe((columns, rows));
        Should.Throw<ArgumentOutOfRangeException>(() => PhotoSheetLayout.Grid(5));
    }

    [Fact(DisplayName = "Листы: 7 снимков по 4 — два листа (4 и 3), снимки по порядку, ячейки в печатаемой области и не пересекаются")]
    public void Pages_split_photos_in_order()
    {
        var pages = PhotoSheetLayout.Pages(7, 4);

        pages.Select(p => p.Cells.Count).ShouldBe([4, 3]);
        pages.SelectMany(p => p.Cells).Select(c => c.Index).ShouldBe([0, 1, 2, 3, 4, 5, 6]);
        PhotoSheetLayout.PageCount(7, 4).ShouldBe(2);
        PhotoSheetLayout.PageCount(20, 20).ShouldBe(1);
        PhotoSheetLayout.PageCount(21, 20).ShouldBe(2);

        var area = PhotoSheetLayout.ContentArea;
        foreach (var cell in pages[0].Cells)
        {
            cell.Image.X.ShouldBeGreaterThanOrEqualTo(area.X);
            (cell.Image.X + cell.Image.Width).ShouldBeLessThanOrEqualTo(area.X + area.Width + 1e-9);
            (cell.Caption.Y + cell.Caption.Height).ShouldBeLessThanOrEqualTo(area.Y + area.Height + 1e-9);
            cell.Caption.Y.ShouldBe(cell.Image.Y + cell.Image.Height, 1e-9); // подпись — сразу под снимком
            cell.Caption.Width.ShouldBe(cell.Image.Width, 1e-9);
        }

        // Соседние ячейки первой строки разделены промежутком.
        var first = pages[0].Cells[0].Image;
        var second = pages[0].Cells[1].Image;
        (second.X - (first.X + first.Width)).ShouldBe(PhotoSheetLayout.GapMm, 1e-9);
    }

    [Fact(DisplayName = "Один снимок «в произвольном размере»: заданный прямоугольник по центру сверху; вне листа или не при одном на лист — отказ")]
    public void Single_size_is_centered_and_bounded()
    {
        var page = PhotoSheetLayout.Pages(2, 1, new PhotoSheetSize(100, 150))[0];
        var cell = page.Cells.ShouldHaveSingleItem();
        cell.Image.Width.ShouldBe(100);
        cell.Image.Height.ShouldBe(150);
        (cell.Image.X + (cell.Image.Width / 2)).ShouldBe(PhotoSheetLayout.PageWidthMm / 2, 1e-9);
        cell.Image.Y.ShouldBe(PhotoSheetLayout.ContentArea.Y);

        PhotoSheetLayout.FitsSingle(new PhotoSheetSize(PhotoSheetLayout.MaxSingleWidthMm, PhotoSheetLayout.MaxSingleHeightMm)).ShouldBeTrue();
        PhotoSheetLayout.FitsSingle(new PhotoSheetSize(PhotoSheetLayout.MaxSingleWidthMm + 1, 100)).ShouldBeFalse();
        PhotoSheetLayout.FitsSingle(new PhotoSheetSize(19, 100)).ShouldBeFalse();
        Should.Throw<ArgumentOutOfRangeException>(() => PhotoSheetLayout.Pages(2, 2, new PhotoSheetSize(100, 150)));
        Should.Throw<ArgumentOutOfRangeException>(() => PhotoSheetLayout.Pages(1, 1, new PhotoSheetSize(300, 150)));
    }

    [Fact(DisplayName = "Вписывание: пропорции сохраняются, снимок по центру ячейки; 300 точек на дюйм; цвет — только #RRGGBB")]
    public void Fit_keeps_proportions()
    {
        var box = new SheetRect(10, 20, 100, 100);
        var wide = PhotoSheetLayout.Fit(box, 400, 200);
        wide.Width.ShouldBe(100);
        wide.Height.ShouldBe(50);
        wide.Y.ShouldBe(45);
        var tall = PhotoSheetLayout.Fit(box, 100, 400);
        tall.Width.ShouldBe(25);
        tall.X.ShouldBe(47.5);

        PhotoSheetLayout.PixelsAt300Dpi(25.4).ShouldBe(300);
        PhotoSheetLayout.IsColor("#1a2B3c").ShouldBeTrue();
        PhotoSheetLayout.IsColor("red").ShouldBeFalse();
        PhotoSheetLayout.IsColor("#12345").ShouldBeFalse();
        PhotoSheetLayout.IsColor(null).ShouldBeFalse();
    }

    [Fact(DisplayName = "Skia: снимок ужимается с пропорциями и не увеличивается; прозрачное — на белом; пометки без подписи — без полосы")]
    public void Skia_fit_and_annotated_without_strip()
    {
        var tools = new SkiaImageTools();

        using (var fitted = Decode(tools.FitJpeg(Png(400, 200, (_, _) => SKColors.Blue), 100, 100)))
        {
            fitted.Width.ShouldBe(100);
            fitted.Height.ShouldBe(50);
        }

        using (var small = Decode(tools.FitJpeg(Png(40, 30, (_, _) => SKColors.Transparent), 100, 100)))
        {
            small.Width.ShouldBe(40);
            small.Height.ShouldBe(30);
            var pixel = small.GetPixel(20, 15);
            pixel.Red.ShouldBeGreaterThan((byte)240); // прозрачное стало белым, а не чёрным
        }

        using var marked = Decode(tools.RenderAnnotated(Png(60, 40, (_, _) => SKColors.White), [], caption: null));
        marked.Height.ShouldBe(40);
    }

    [Fact(DisplayName = "Skia PDF: листов — по раскладке; на каждом гриф, заголовок, «лист N из M» и «Выдал»; свойства файла — заголовок, исполнитель, гриф")]
    public void Skia_renders_marked_pdf()
    {
        var tools = new SkiaImageTools();
        var photo = tools.FitJpeg(Png(80, 60, (x, _) => x < 40 ? SKColors.Red : SKColors.Green), 200, 200);
        var document = new PhotoSheetDocument(
            Enumerable.Range(0, 5).Select(i => new PhotoSheetPhoto(photo, i == 0 ? "Шторм, 05.10.2026, рынок «Ош»" : null, "#FFFFFF", "#C62828")).ToList(),
            4,
            null,
            "Фототаблица к справке",
            "СЕКРЕТНО",
            "Петров П.П.",
            new DateTime(2026, 10, 21, 9, 30, 0));

        var pdf = tools.RenderPhotoSheetPdf(document);

        using var reader = PdfDocument.Open(pdf);
        reader.NumberOfPages.ShouldBe(2);
        reader.Information.Title.ShouldBe("Фототаблица к справке");
        reader.Information.Author.ShouldBe("Петров П.П.");
        reader.Information.Subject.ShouldBe("СЕКРЕТНО");
        foreach (var page in reader.GetPages())
        {
            var text = page.Text;
            text.ShouldContain("СЕКРЕТНО");
            text.ShouldContain("Выдал: Петров П.П. · 21.10.2026 09:30");
            text.ShouldContain($"лист {page.Number} из 2");
            page.GetImages().ShouldNotBeEmpty();
        }

        reader.GetPage(1).Text.ShouldContain("Шторм");
    }

    [Fact(DisplayName = "Выгрузка: снимки ужимаются под ячейку, пометки — только по отметке; гриф листа — максимум; «Выдал» — ФИО; имя файла без запрещённых знаков")]
    public async Task Export_builds_document()
    {
        var kit = new Kit();
        kit.Annotations.ListAsync(6, Access, Arg.Any<CancellationToken>())
            .Returns([new AnnotationRow(1, 6, AnnotationKind.Arrow, 1, 1, 9, 9, null, AnnotationColors.Red, 42, DateTime.UtcNow)]);
        var query = new ExportPhotoSheetQuery(
            [new PhotoSheetEntry(5, " Шторм ", "#FFFFFF", "#000000"), new PhotoSheetEntry(6, null, WithAnnotations: true)], 2, Title: "К справке: № 1/2");

        var response = await kit.Handle(query);

        var file = response.Data.ShouldNotBeNull();
        file.Content.ShouldBe(new byte[] { 37, 80, 68, 70 });
        file.Classification.ShouldBe<short>(3);
        file.Pages.ShouldBe(1);
        file.FileName.ShouldBe("К справке_ № 1_2 21.10.2026 09-30.pdf");
        kit.Document.ShouldNotBeNull().Marking.ShouldBe(ClassificationLevels.Marking(3));
        kit.Document.Executor.ShouldBe("Петров П.П.");
        kit.Document.Title.ShouldBe("К справке: № 1/2");
        kit.Document.Photos.Select(p => p.Caption).ShouldBe(["Шторм", null]);

        // Ячейка сетки 1×2 — ужатие до её размера при 300 точках на дюйм; пометки — только у второго снимка.
        var box = PhotoSheetLayout.Pages(2, 2)[0].Cells[0].Image;
        kit.Tools.Received(2).FitJpeg(Arg.Any<byte[]>(), PhotoSheetLayout.PixelsAt300Dpi(box.Width), PhotoSheetLayout.PixelsAt300Dpi(box.Height), 90);
        kit.Tools.Received(1).RenderAnnotated(Arg.Any<byte[]>(), Arg.Any<IReadOnlyList<AnnotationRow>>(), Arg.Is<string?>(caption => caption == null));
        await kit.Annotations.DidNotReceive().ListAsync(5, Arg.Any<AccessContext>(), Arg.Any<CancellationToken>());

        query.AuditSummary.ShouldBe("media:photo-sheet:export;assets=5,6;per-page=2;annotated=1");
        query.AuditSummary.ShouldNotBeNull().ShouldNotContain("Шторм");
    }

    [Fact(DisplayName = "Выгрузка: снимок чужого дела или не фото — отказ всей выгрузки одним ответом, файлы не читаются; без сотрудника — отказ")]
    public async Task Export_refuses_foreign_and_anonymous()
    {
        var kit = new Kit();
        kit.CaseScope.IsAssetAccessibleAsync(6, Access, Arg.Any<CancellationToken>()).Returns(false);

        var foreign = await kit.Handle(new ExportPhotoSheetQuery([new PhotoSheetEntry(5, null), new PhotoSheetEntry(6, null)], 2));

        foreign.StatusCode.ShouldBe(ResponseStatusCode.NotFound);
        foreign.StatusMessage.ShouldBe(PhotoSheetMessages.NotFound);
        await kit.Storage.DidNotReceiveWithAnyArgs().OpenReadAsync(default!, default!, default!, default);

        kit.CaseScope.IsAssetAccessibleAsync(6, Access, Arg.Any<CancellationToken>()).Returns(true);
        kit.Catalog.GetAsync(6, Access, Arg.Any<CancellationToken>()).Returns(Asset(6, 1) with { Kind = MediaKind.Video });
        (await kit.Handle(new ExportPhotoSheetQuery([new PhotoSheetEntry(6, null)], 1))).StatusMessage.ShouldBe(PhotoSheetMessages.NotFound);

        kit.Subject.GetCurrentUserIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);
        (await kit.Handle(new ExportPhotoSheetQuery([new PhotoSheetEntry(5, null)], 1))).StatusMessage.ShouldBe(PhotoSheetMessages.NoExecutor);
        kit.Tools.DidNotReceiveWithAnyArgs().RenderPhotoSheetPdf(default!);
    }

    [Fact(DisplayName = "Правила формы: 1–100 снимков, число на лист из перечня, цвета #RRGGBB, произвольный размер — обе стороны, один на лист, в пределах листа")]
    public void Validator_rules()
    {
        var validator = new ExportPhotoSheetValidator();
        PhotoSheetEntry[] one = [new(5, "подпись")];
        validator.Validate(new ExportPhotoSheetQuery(one, 4)).IsValid.ShouldBeTrue();
        validator.Validate(new ExportPhotoSheetQuery(one, 1, 100, 150)).IsValid.ShouldBeTrue();

        validator.Validate(new ExportPhotoSheetQuery([], 4)).IsValid.ShouldBeFalse();
        validator.Validate(new ExportPhotoSheetQuery(Enumerable.Range(1, 101).Select(i => new PhotoSheetEntry(i, null)).ToList(), 20)).IsValid.ShouldBeFalse();
        validator.Validate(new ExportPhotoSheetQuery(one, 3)).IsValid.ShouldBeFalse();
        validator.Validate(new ExportPhotoSheetQuery([new(5, null, "red")], 4)).IsValid.ShouldBeFalse();
        validator.Validate(new ExportPhotoSheetQuery([new(5, new string('п', PhotoSheetLayout.MaxCaptionLength + 1))], 4)).IsValid.ShouldBeFalse();
        validator.Validate(new ExportPhotoSheetQuery(one, 1, 100, null)).IsValid.ShouldBeFalse();
        validator.Validate(new ExportPhotoSheetQuery(one, 2, 100, 150)).IsValid.ShouldBeFalse();
        validator.Validate(new ExportPhotoSheetQuery(one, 1, 500, 150)).IsValid.ShouldBeFalse();
        validator.Validate(new ExportPhotoSheetQuery([new(0, null)], 4)).IsValid.ShouldBeFalse();
    }

    [Fact(DisplayName = "Страница печати: из названных номеров — только фото своих дел, с происхождением и числом пометок; журнал — номера")]
    public async Task Printable_list_filters_and_labels()
    {
        var kit = new Kit();
        kit.Catalog.GetAsync(7, Access, Arg.Any<CancellationToken>()).Returns(Asset(7, 1) with { EditOperations = "{}" });
        kit.CaseScope.IsAssetAccessibleAsync(7, Access, Arg.Any<CancellationToken>()).Returns(true);
        kit.Catalog.GetAsync(8, Access, Arg.Any<CancellationToken>()).Returns(Asset(8, 1) with { SourceAssetId = 3 });
        kit.CaseScope.IsAssetAccessibleAsync(8, Access, Arg.Any<CancellationToken>()).Returns(true);
        kit.CaseScope.IsAssetAccessibleAsync(6, Access, Arg.Any<CancellationToken>()).Returns(false);
        kit.Annotations.ListAsync(5, Access, Arg.Any<CancellationToken>())
            .Returns([new AnnotationRow(1, 5, AnnotationKind.Arrow, 1, 1, 9, 9, null, AnnotationColors.Red, 42, DateTime.UtcNow)]);
        var query = new ListPrintableAssetsQuery([5, 6, 7, 8, 99]);

        var rows = (await new ListPrintableAssetsQuery.Handler(kit.AccessProvider, kit.Catalog, kit.CaseScope, kit.Annotations)
            .Handle(query, CancellationToken.None)).Data.ShouldNotBeNull();

        rows.Select(r => (r.AssetId, r.Origin, r.AnnotationCount)).ShouldBe([(5, "фото", 1), (7, "обработанная копия", 0), (8, "снимок кадра", 0)]);
        query.AuditSummary.ShouldBe("media:photo-sheet:view;assets=5,6,7,8,99");
        new ListPrintableAssetsValidator().Validate(new ListPrintableAssetsQuery([])).IsValid.ShouldBeFalse();
    }

    private static MediaAssetRow Asset(int id, short classification) => new(
        id, MediaKind.Image, $"снимок {id}.jpg", $"{new string('a', 31)}{id}.jpg", "image/jpeg", 1024, null, null, null,
        classification, 7, 1, MediaIndexStatus.Indexed, null, null, null, null, DateTime.UtcNow, 0);

    private static byte[] Png(int width, int height, Func<int, int, SKColor> color)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                bitmap.SetPixel(x, y, color(x, y));
            }
        }

        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static SKBitmap Decode(byte[] bytes) => SKBitmap.Decode(bytes).Copy(SKColorType.Rgba8888);

    private sealed class FixedTime : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 10, 21, 9, 30, 0, TimeSpan.Zero);

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    private sealed class Kit
    {
        public Kit()
        {
            AccessProvider.GetCurrentAsync(Arg.Any<CancellationToken>()).Returns(Access);
            Catalog.GetAsync(5, Access, Arg.Any<CancellationToken>()).Returns(Asset(5, 1));
            Catalog.GetAsync(6, Access, Arg.Any<CancellationToken>()).Returns(Asset(6, 3));
            CaseScope.IsAssetAccessibleAsync(5, Access, Arg.Any<CancellationToken>()).Returns(true);
            CaseScope.IsAssetAccessibleAsync(6, Access, Arg.Any<CancellationToken>()).Returns(true);
            Annotations.ListAsync(Arg.Any<int>(), Access, Arg.Any<CancellationToken>()).Returns([]);
            Storage.OpenReadAsync(Arg.Any<string>(), MediaFileCategories.Originals, Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(_ => new MemoryStream([1, 2, 3]));
            Subject.GetCurrentUserIdAsync(Arg.Any<CancellationToken>()).Returns(42);
            Accounts.ListAsync(Arg.Any<CancellationToken>()).Returns([new UserAccountRow(42, "petrov", "Петров П.П.", true, true, false)]);
            Tools.RenderAnnotated(Arg.Any<byte[]>(), Arg.Any<IReadOnlyList<AnnotationRow>>(), Arg.Any<string?>()).Returns([8, 8]);
            Tools.FitJpeg(Arg.Any<byte[]>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int>()).Returns([9, 9]);
            Tools.RenderPhotoSheetPdf(Arg.Do<PhotoSheetDocument>(d => Document = d)).Returns([37, 80, 68, 70]);
        }

        public IAccessContextProvider AccessProvider { get; } = Substitute.For<IAccessContextProvider>();

        public IMediaCatalog Catalog { get; } = Substitute.For<IMediaCatalog>();

        public ICaseScope CaseScope { get; } = Substitute.For<ICaseScope>();

        public IMediaAnnotationStore Annotations { get; } = Substitute.For<IMediaAnnotationStore>();

        public IFileStorage Storage { get; } = Substitute.For<IFileStorage>();

        public IImageTools Tools { get; } = Substitute.For<IImageTools>();

        public ISubjectProvider Subject { get; } = Substitute.For<ISubjectProvider>();

        public IUserAccountStore Accounts { get; } = Substitute.For<IUserAccountStore>();

        public PhotoSheetDocument? Document { get; private set; }

        public async Task<ResponseDto<PhotoSheetFile>> Handle(ExportPhotoSheetQuery query) =>
            await new ExportPhotoSheetQuery.Handler(AccessProvider, Catalog, CaseScope, Annotations, Storage, Tools, Subject, Accounts, new FixedTime())
                .Handle(query, CancellationToken.None);
    }
}
