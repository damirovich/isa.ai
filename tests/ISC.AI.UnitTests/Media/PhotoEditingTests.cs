using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.BackgroundTasks;
using ISC.AI.Abstractions.Security;
using ISC.AI.Abstractions.Storage;
using ISC.AI.Modules.Media.Application.Features.Annotations;
using ISC.AI.Modules.Media.Application.Features.Assets.Commands.EditPhoto;
using ISC.AI.Modules.Media.Application.Features.Assets;
using ISC.AI.Modules.Media.Domain.Model;
using ISC.AI.Modules.Media.Domain.Services;
using ISC.AI.Vision.Onnx.Imaging;
using NSubstitute;
using Shouldly;
using SkiaSharp;
using Xunit;

namespace ISC.AI.UnitTests.Media;

/// <summary>
/// Обработка фото и пометки (ТФ-МЕД-13/14, ADR-0050): набор операций — проверка, перечень для человека, JSON; геометрия
/// поворота и «зум лица»; настоящий Skia — поворот пиксель в пиксель, поворот по EXIF, кадрирование, увеличение, тон и
/// резкость по формулам предпросмотра; копия сохраняется отдельным носителем с перечнем операций и не индексируется;
/// отказы (не фото, копия, пустой набор, вне дел); пометки — проверка, права, «только просмотр», журнал без текста;
/// выгрузка с пометками — гриф и «кто выгрузил».
/// </summary>
public sealed class PhotoEditingTests
{
    private static readonly AccessContext Access = new("42", 2, [7]);

    // ---------------- набор операций ----------------

    [Fact(DisplayName = "Набор операций: перечень по порядку применения, пустой — «без обработки», JSON туда и обратно")]
    public void Edit_describes_and_round_trips()
    {
        var edit = new ImageEdit(Brightness: 20, Contrast: -10, Gamma: 1.4, Sharpen: 30, Rotation: 90, Crop: new CropRect(10, 20, 640, 480), Zoom: 2);

        edit.Describe().ShouldBe("поворот 90°; кадрирование 640×480 от (10, 20); увеличение ×2; яркость +20; контрастность −10; гамма 1,4; резкость 30");
        new ImageEdit().Describe().ShouldBe("без обработки");
        new ImageEdit().IsIdentity.ShouldBeTrue();
        edit.IsIdentity.ShouldBeFalse();
        ImageEdit.FromJson(edit.ToJson()).ShouldBe(edit);
        ImageEdit.FromJson("{сломано").ShouldBeNull();
        ImageEdit.FromJson(null).ShouldBeNull();
    }

    [Fact(DisplayName = "Проверка: пределы ползунков, поворот кратен 90°, кадрирование внутри повёрнутого изображения, результат не больше 40 Мп")]
    public void Edit_validates_against_image()
    {
        new ImageEdit(Brightness: 20, Rotation: 90, Crop: new CropRect(0, 0, 300, 400)).Validate(400, 300).ShouldBeEmpty();
        new ImageEdit(Brightness: 101).Validate(10, 10).ShouldContain("Яркость — от −100 до 100.");
        new ImageEdit(Gamma: 0.1).Validate(10, 10).ShouldContain("Гамма — от 0,3 до 3.");
        new ImageEdit(Rotation: 45).Validate(10, 10).ShouldContain("Поворот — на 0, 90, 180 или 270 градусов.");
        new ImageEdit(Crop: new CropRect(0, 0, 400, 300), Rotation: 90).Validate(400, 300)
            .ShouldContain("Область кадрирования выходит за границы изображения.");
        new ImageEdit(Zoom: 4).Validate(4000, 3000).ShouldContain("Результат слишком велик — уменьшите увеличение или область кадрирования.");
        new ImageEdit(Zoom: 4).OutputSize(400, 300).ShouldBe((1600, 1200));
    }

    [Theory(DisplayName = "Геометрия: прямоугольник после поворота на 90/180/270° (изображение 400×300)")]
    [InlineData(0, 10, 20, 30, 40)]
    [InlineData(90, 240, 10, 40, 30)]
    [InlineData(180, 360, 240, 30, 40)]
    [InlineData(270, 20, 360, 40, 30)]
    public void Rect_rotates_with_image(int rotation, int x, int y, int width, int height) =>
        ImageEditGeometry.Rotate(new CropRect(10, 20, 30, 40), rotation, 400, 300).ShouldBe(new CropRect(x, y, width, height));

    [Fact(DisplayName = "«Зум лица»: рамка с полями 25 %, обрезанная по краю, увеличение до 512 по длинной стороне, но не больше ×4")]
    public void Face_zoom_adds_margin_and_picks_zoom()
    {
        var (crop, zoom) = ImageEditGeometry.FaceZoom(100, 100, 80, 80, rotation: 0, width: 1000, height: 800);
        crop.ShouldBe(new CropRect(80, 80, 120, 120));
        zoom.ShouldBe(4); // 512 / 120 ≈ 4,3 → предел 4

        var (edge, bigZoom) = ImageEditGeometry.FaceZoom(0, 0, 400, 400, rotation: 0, width: 450, height: 450);
        edge.ShouldBe(new CropRect(0, 0, 450, 450)); // поля обрезаны краем изображения
        bigZoom.ShouldBe(2);                          // 512 / 450 → 2
    }

    // ---------------- Skia ----------------

    [Fact(DisplayName = "Skia: поворот на 90° — пиксель в пиксель (угол переезжает), кадрирование и увеличение ×2 дают точный размер")]
    public void Skia_rotates_crops_and_zooms_exactly()
    {
        // 3×2: левый верхний — красный, правый верхний — зелёный, остальные — синие.
        var png = Png(3, 2, (x, y) => (x, y) switch { (0, 0) => SKColors.Red, (2, 0) => SKColors.Lime, _ => SKColors.Blue });
        var tools = new SkiaImageTools();

        using var rotated = Decode(tools.ApplyEdits(png, new ImageEdit(Rotation: 90)));
        (rotated.Width, rotated.Height).ShouldBe((2, 3));
        rotated.GetPixel(1, 0).ShouldBe(SKColors.Red);  // левый верхний → правый верхний
        rotated.GetPixel(1, 2).ShouldBe(SKColors.Lime); // правый верхний → правый нижний

        using var cropped = Decode(tools.ApplyEdits(png, new ImageEdit(Crop: new CropRect(2, 0, 1, 1), Zoom: 2)));
        (cropped.Width, cropped.Height).ShouldBe((2, 2));
        cropped.GetPixel(0, 0).Green.ShouldBeGreaterThan((byte)200);
    }

    [Fact(DisplayName = "Skia: метка EXIF учитывается — координаты как у браузера; тон — по той же таблице, что предпросмотр")]
    public void Skia_applies_exif_and_tone_table()
    {
        var tools = new SkiaImageTools();
        var jpeg = ExifRotatedJpeg(); // 4×2 в файле, метка «повернуть на 90°» → 2×4 на экране
        using var oriented = Decode(tools.ApplyEdits(jpeg, new ImageEdit(Brightness: 1)));
        (oriented.Width, oriented.Height).ShouldBe((2, 4));

        var gray = Png(1, 1, (_, _) => new SKColor(100, 100, 100));
        using var toned = Decode(tools.ApplyEdits(gray, new ImageEdit(Brightness: 20, Contrast: 10, Gamma: 1.5)));
        var expected = ImageEditing.ToneTable(20, 10, 1.5)[100];
        toned.GetPixel(0, 0).Red.ShouldBe(expected);
        ImageEditing.ToneTable(0, 0, 1.0).ShouldBe(Enumerable.Range(0, 256).Select(i => (byte)i).ToArray());
    }

    [Fact(DisplayName = "Skia: резкость поднимает перепад у края; недопустимый набор — ArgumentException с причиной")]
    public void Skia_sharpens_and_rejects_invalid()
    {
        var tools = new SkiaImageTools();
        var edge = Png(4, 1, (x, _) => x < 2 ? new SKColor(100, 100, 100) : new SKColor(150, 150, 150));
        using var sharp = Decode(tools.ApplyEdits(edge, new ImageEdit(Sharpen: 100)));
        sharp.GetPixel(1, 0).Red.ShouldBeLessThan((byte)100);
        sharp.GetPixel(2, 0).Red.ShouldBeGreaterThan((byte)150);

        Should.Throw<ArgumentException>(() => tools.ApplyEdits(edge, new ImageEdit(Crop: new CropRect(0, 0, 10, 10))))
            .Message.ShouldContain("Область кадрирования выходит за границы изображения.");
    }

    [Fact(DisplayName = "Skia: выгрузка с пометками — полоса подписи снизу, рамка нанесена её цветом, размер — изображение + полоса")]
    public void Skia_renders_annotations_with_caption()
    {
        var tools = new SkiaImageTools();
        var white = Png(200, 100, (_, _) => SKColors.White);
        var marks = new List<AnnotationRow>
        {
            new(1, 5, AnnotationKind.Rectangle, 10, 10, 60, 60, null, AnnotationColors.Red, 42, DateTime.UtcNow),
            new(2, 5, AnnotationKind.Text, 100, 50, 100, 50, "Здесь", AnnotationColors.Blue, 42, DateTime.UtcNow),
        };

        using var result = Decode(tools.RenderAnnotated(white, marks, "НЕСЕКРЕТНО · носитель № 5"));

        result.Width.ShouldBe(200);
        result.Height.ShouldBeGreaterThan(100);
        var stroke = result.GetPixel(10, 30);
        stroke.Red.ShouldBeGreaterThan((byte)200);
        stroke.Green.ShouldBeLessThan((byte)120);
        result.GetPixel(150, 5).ShouldBe(SKColors.White); // вне пометок изображение не тронуто
    }

    // ---------------- копия ----------------

    [Fact(DisplayName = "Копия: из оригинала — новый носитель того же дела с грифом источника, перечнем операций и ссылкой; журнал — на копию")]
    public async Task Copy_is_received_as_new_asset_with_operations()
    {
        var kit = new CopyKit();
        var edit = new ImageEdit(Brightness: 20);

        var response = await kit.HandleAsync(edit);

        response.Data.ShouldNotBeNull().AssetId.ShouldBe(77);
        kit.Draft.ShouldNotBeNull();
        kit.Draft.SourceAssetId.ShouldBe(5);
        kit.Draft.EditOperations.ShouldBe(edit.ToJson());
        kit.Draft.Classification.ShouldBe((short)2);
        kit.Draft.DivisionId.ShouldBe(7);
        kit.Draft.OriginalFileName.ShouldBe("снимок (обработка).png");
        kit.Draft.Source.ShouldBe("Обработанная копия носителя № 5: яркость +20");
        await kit.CaseScope.Received(1).LinkAssetAsync(3, 77, null, 42, Arg.Any<CancellationToken>());
        await kit.Audit.Received(1).WriteAsync(
            Arg.Is<AuditEntry>(e => e.Action == AuditAction.Ingest && e.ObjectRef == "media:asset:77:edited-copy" && e.Classification == 2
                && e.PayloadSensitive!.Contains("операции: яркость +20")),
            Arg.Any<CancellationToken>());
        new CreateEditedCopyCommand(5, edit).AuditSummary.ShouldBe("media:edited-copy:source=5;ops=яркость +20");
    }

    [Fact(DisplayName = "Копия: не фото, уже копия, пустой набор, вне дел субъекта, без права — отказ; хранилище не трогается")]
    public async Task Copy_refusals()
    {
        var kit = new CopyKit();
        (await kit.HandleAsync(new ImageEdit())).StatusMessage.ShouldBe(CreateEditedCopyCommand.IdentityMessage);

        kit.Asset = kit.Asset with { Kind = MediaKind.Video };
        (await kit.HandleAsync(new ImageEdit(Zoom: 2))).StatusMessage.ShouldBe(CreateEditedCopyCommand.NotImageMessage);

        kit.Asset = kit.Asset with { Kind = MediaKind.Image, EditOperations = new ImageEdit(Sharpen: 5).ToJson() };
        (await kit.HandleAsync(new ImageEdit(Zoom: 2))).StatusMessage.ShouldBe(CreateEditedCopyCommand.AlreadyCopyMessage);

        kit.Asset = kit.Asset with { EditOperations = null };
        kit.CaseScope.IsAssetAccessibleAsync(5, Access, Arg.Any<CancellationToken>()).Returns(false);
        (await kit.HandleAsync(new ImageEdit(Zoom: 2))).StatusMessage.ShouldBe(CreateEditedCopyCommand.NotFoundMessage);

        kit.Administration.CanUploadAsync(Arg.Any<CancellationToken>()).Returns(false);
        (await kit.HandleAsync(new ImageEdit(Zoom: 2))).StatusMessage.ShouldBe(CreateEditedCopyCommand.NoRightMessage);

        await kit.Store.DidNotReceiveWithAnyArgs().ReceiveAsync(default!, default!, default);
    }

    [Fact(DisplayName = "Копию не переиндексировать: явный отказ, задача индексации не ставится")]
    public async Task Copy_is_not_reindexed()
    {
        var kit = new CopyKit();
        kit.Asset = kit.Asset with { EditOperations = new ImageEdit(Zoom: 2).ToJson(), IndexStatus = MediaIndexStatus.NotApplicable };
        var queue = Substitute.For<IBackgroundTaskQueue>();

        var response = await new ReindexMediaCommand.Handler(kit.Administration, kit.AccessProvider, kit.Catalog, kit.CaseScope, queue)
            .Handle(new ReindexMediaCommand(5), CancellationToken.None);

        response.StatusMessage.ShouldBe(ReindexMediaCommand.EditedCopyNotIndexedMessage);
        await queue.DidNotReceiveWithAnyArgs().EnqueueAsync(default!, default!, default);
    }

    // ---------------- пометки ----------------

    [Fact(DisplayName = "Пометка: проверка вида, размера, текста и цвета палитры")]
    public void Annotation_draft_validates()
    {
        new AnnotationDraft(5, AnnotationKind.Arrow, 1, 1, 50, 50, null, AnnotationColors.Red).Validate().ShouldBeEmpty();
        new AnnotationDraft(5, AnnotationKind.Rectangle, 1, 1, 2, 2, null, AnnotationColors.Red).Validate()
            .ShouldContain("Пометка слишком мала — протяните мышью.");
        new AnnotationDraft(5, AnnotationKind.Text, 1, 1, 1, 1, " ", AnnotationColors.Red).Validate().ShouldContain("Введите текст подписи.");
        new AnnotationDraft(5, AnnotationKind.Text, 1, 1, 1, 1, "ок", "#123456").Validate().ShouldContain("Цвет — из палитры.");
        new AnnotationDraft(5, AnnotationKind.Arrow, double.NaN, 1, 50, 50, null, AnnotationColors.Red).Validate()
            .ShouldContain("Координаты пометки — вне изображения.");
        new AnnotationDraft(5, AnnotationKind.Text, 1, 1, 1, 1, new string('я', 201), AnnotationColors.Red).Validate()
            .ShouldContain("Текст подписи — не длиннее 200 знаков.");
    }

    [Fact(DisplayName = "Пометка: ставится на носитель своего дела от имени сотрудника; вне дел — «не найдено»; без права — отказ; журнал без текста")]
    public async Task Annotation_add_rules()
    {
        var administration = Substitute.For<IMediaAdministration>();
        var accessProvider = Substitute.For<IAccessContextProvider>();
        var caseScope = Substitute.For<ICaseScope>();
        var store = Substitute.For<IMediaAnnotationStore>();
        administration.CanUploadAsync(Arg.Any<CancellationToken>()).Returns(true);
        accessProvider.GetCurrentAsync(Arg.Any<CancellationToken>()).Returns(Access);
        caseScope.IsAssetAccessibleAsync(5, Access, Arg.Any<CancellationToken>()).Returns(true);
        store.AddAsync(Arg.Any<AnnotationDraft>(), 42, Access, Arg.Any<CancellationToken>()).Returns(9);
        var handler = new AddAnnotationCommand.Handler(administration, accessProvider, caseScope, store);
        var draft = new AnnotationDraft(5, AnnotationKind.Text, 10, 10, 10, 10, "секретная подпись", AnnotationColors.Red);

        (await handler.Handle(new AddAnnotationCommand(draft), CancellationToken.None)).Data.ShouldBe(9);
        new AddAnnotationCommand(draft).AuditSummary.ShouldBe("media:asset:5:annotation:add;kind=Text");
        new AddAnnotationCommand(draft).AuditSummary.ShouldNotContain("секретная");

        caseScope.IsAssetAccessibleAsync(5, Access, Arg.Any<CancellationToken>()).Returns(false);
        (await handler.Handle(new AddAnnotationCommand(draft), CancellationToken.None)).StatusMessage.ShouldBe(AnnotationMessages.NotFound);

        administration.CanUploadAsync(Arg.Any<CancellationToken>()).Returns(false);
        (await handler.Handle(new AddAnnotationCommand(draft), CancellationToken.None)).StatusMessage.ShouldBe(AddAnnotationCommand.NoRightMessage);
        await store.ReceivedWithAnyArgs(1).AddAsync(default!, default, default!, default);
    }

    [Fact(DisplayName = "Подпись выгрузки: маркировка грифа, носитель, число пометок, кто и когда")]
    public void Export_caption_has_marking()
    {
        var asset = new CopyKit().Asset;
        AnnotationMessages.Caption(asset, 3, "Петров П.П.", new DateTime(2026, 10, 15, 14, 5, 0))
            .ShouldBe("СЕКРЕТНО · носитель № 5 «снимок.jpg» · пометок: 3 · Петров П.П. · 15.10.2026 14:05");
        new ExportAnnotatedImageQuery(5).AuditAction.ShouldBe(AuditAction.Export);
    }

    [Fact(DisplayName = "Выгрузка с пометками: подпись — гриф, носитель, число пометок, ФИО и время; вне своего дела и без сотрудника — отказ")]
    public async Task Export_builds_caption_and_requires_own_case()
    {
        var kit = new CopyKit();
        var store = Substitute.For<IMediaAnnotationStore>();
        var marks = new List<AnnotationRow> { new(1, 5, AnnotationKind.Arrow, 1, 1, 9, 9, null, AnnotationColors.Red, 42, DateTime.UtcNow) };
        store.ListAsync(5, Access, Arg.Any<CancellationToken>()).Returns(marks);
        var subject = Substitute.For<ISubjectProvider>();
        subject.GetCurrentUserIdAsync(Arg.Any<CancellationToken>()).Returns(42);
        var accounts = Substitute.For<IUserAccountStore>();
        accounts.ListAsync(Arg.Any<CancellationToken>()).Returns([new UserAccountRow(42, "petrov", "Петров П.П.", true, true, false)]);
        kit.ImageTools.RenderAnnotated(Arg.Any<byte[]>(), marks, Arg.Any<string>()).Returns([7, 7]);
        var handler = new ExportAnnotatedImageQuery.Handler(
            kit.AccessProvider, kit.Catalog, kit.CaseScope, store, kit.Storage, kit.ImageTools, subject, accounts, new FixedTime());

        var response = await handler.Handle(new ExportAnnotatedImageQuery(5), CancellationToken.None);

        response.Data.ShouldNotBeNull().FileName.ShouldBe("снимок (с пометками).png");
        response.Data.Content.ShouldBe(new byte[] { 7, 7 });
        kit.ImageTools.Received(1).RenderAnnotated(
            Arg.Any<byte[]>(), marks, "СЕКРЕТНО · носитель № 5 «снимок.jpg» · пометок: 1 · Петров П.П. · 15.10.2026 09:30");

        subject.GetCurrentUserIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);
        (await handler.Handle(new ExportAnnotatedImageQuery(5), CancellationToken.None)).StatusMessage.ShouldContain("кто выгрузил");

        kit.CaseScope.IsAssetAccessibleAsync(5, Access, Arg.Any<CancellationToken>()).Returns(false);
        (await handler.Handle(new ExportAnnotatedImageQuery(5), CancellationToken.None)).StatusMessage.ShouldBe(AnnotationMessages.NotFound);
    }

    [Fact(DisplayName = "Снятие пометки: только в своём деле; чужая или невидимая пометка — «не найдено», хранилище не трогается")]
    public async Task Delete_requires_own_case()
    {
        var kit = new CopyKit();
        var store = Substitute.For<IMediaAnnotationStore>();
        store.GetAssetIdAsync(9, Access, Arg.Any<CancellationToken>()).Returns(5);
        store.DeleteAsync(9, Access, Arg.Any<CancellationToken>()).Returns(true);
        var handler = new DeleteAnnotationCommand.Handler(kit.Administration, kit.AccessProvider, kit.CaseScope, store);

        (await handler.Handle(new DeleteAnnotationCommand(9), CancellationToken.None)).Data.ShouldBeTrue();
        (await handler.Handle(new DeleteAnnotationCommand(10), CancellationToken.None)).StatusMessage.ShouldBe(AnnotationMessages.NotFound);

        kit.CaseScope.IsAssetAccessibleAsync(5, Access, Arg.Any<CancellationToken>()).Returns(false);
        (await handler.Handle(new DeleteAnnotationCommand(9), CancellationToken.None)).StatusMessage.ShouldBe(AnnotationMessages.NotFound);
        await store.Received(1).DeleteAsync(Arg.Any<int>(), Arg.Any<AccessContext>(), Arg.Any<CancellationToken>());
        new DeleteAnnotationCommand(9).AuditSummary.ShouldBe("media:annotation:9:delete");
    }

    // ---------------- обвязка ----------------

    /// <summary>Часы теста: 15.10.2026 09:30, часовой пояс — UTC.</summary>
    private sealed class FixedTime : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 10, 15, 9, 30, 0, TimeSpan.Zero);

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

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

    /// <summary>JPEG 4×2 с меткой EXIF Orientation = 6 («повернуть на 90° по часовой»): сегмент APP1 вставлен после SOI.</summary>
    private static byte[] ExifRotatedJpeg()
    {
        using var bitmap = new SKBitmap(new SKImageInfo(4, 2, SKColorType.Rgba8888, SKAlphaType.Premul));
        bitmap.Erase(SKColors.Gray);
        using var data = bitmap.Encode(SKEncodedImageFormat.Jpeg, 90);
        var jpeg = data.ToArray();
        byte[] exif =
        [
            0xFF, 0xE1, 0x00, 0x22, (byte)'E', (byte)'x', (byte)'i', (byte)'f', 0x00, 0x00,
            (byte)'M', (byte)'M', 0x00, 0x2A, 0x00, 0x00, 0x00, 0x08,
            0x00, 0x01, 0x01, 0x12, 0x00, 0x03, 0x00, 0x00, 0x00, 0x01, 0x00, 0x06, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00,
        ];
        return [.. jpeg.Take(2), .. exif, .. jpeg.Skip(2)];
    }

    private sealed class CopyKit
    {
        public CopyKit()
        {
            Administration.CanUploadAsync(Arg.Any<CancellationToken>()).Returns(true);
            AccessProvider.GetCurrentAsync(Arg.Any<CancellationToken>()).Returns(Access);
            Catalog.GetAsync(5, Access, Arg.Any<CancellationToken>()).Returns(_ => Asset);
            CaseScope.IsAssetAccessibleAsync(5, Access, Arg.Any<CancellationToken>()).Returns(true);
            CaseScope.GetCaseIdForAssetAsync(5, Access, Arg.Any<CancellationToken>()).Returns(3);
            Storage.OpenReadAsync(Arg.Any<string>(), MediaFileCategories.Originals, "5", Arg.Any<CancellationToken>())
                .Returns(_ => new MemoryStream([1, 2, 3]));
            ImageTools.ApplyEdits(Arg.Any<byte[]>(), Arg.Any<ImageEdit>()).Returns([9, 9]);
            Store.ReceiveAsync(Arg.Do<MediaAssetDraft>(d => Draft = d), Arg.Any<Stream>(), Arg.Any<CancellationToken>())
                .Returns(new MediaAssetReceipt(77, false));
        }

        public IMediaAdministration Administration { get; } = Substitute.For<IMediaAdministration>();

        public IAccessContextProvider AccessProvider { get; } = Substitute.For<IAccessContextProvider>();

        public IMediaCatalog Catalog { get; } = Substitute.For<IMediaCatalog>();

        public ICaseScope CaseScope { get; } = Substitute.For<ICaseScope>();

        public IMediaStore Store { get; } = Substitute.For<IMediaStore>();

        public IFileStorage Storage { get; } = Substitute.For<IFileStorage>();

        public IImageTools ImageTools { get; } = Substitute.For<IImageTools>();

        public IAuditWriter Audit { get; } = Substitute.For<IAuditWriter>();

        public MediaAssetDraft? Draft { get; private set; }

        public MediaAssetRow Asset { get; set; } = new(
            5, MediaKind.Image, "снимок.jpg", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.jpg", "image/jpeg", 1024, null, null, null,
            2, 7, 1, MediaIndexStatus.Indexed, null, null, null, null, DateTime.UtcNow, 1);

        public async Task<ISC.AI.Abstractions.Application.ResponseDto<EditedCopyResult>> HandleAsync(ImageEdit edit) =>
            await new CreateEditedCopyCommand.Handler(Administration, AccessProvider, Catalog, CaseScope, Store, Storage, ImageTools, Audit)
                .Handle(new CreateEditedCopyCommand(5, edit), CancellationToken.None);
    }
}
