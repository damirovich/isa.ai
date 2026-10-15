using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.Media.Application.Features.Assets.Commands.EditPhoto;
using ISC.AI.Modules.Media.Data;
using ISC.AI.Modules.Media.Domain.Model;
using ISC.AI.Modules.Media.Domain.Services;
using ISC.AI.Persistence.Audit;
using ISC.AI.Persistence.Storage;
using ISC.AI.Profile.Investigation.Domain.Enums;
using ISC.AI.Profile.Investigation.Domain.Services;
using ISC.AI.Vision.Onnx.Imaging;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Shouldly;
using SkiaSharp;
using Xunit;

namespace ISC.AI.IntegrationTests.Persistence;

/// <summary>
/// Обработка фото и пометки (ТФ-МЕД-13/14, ADR-0050) на настоящей БД: схемы <c>media</c> (носители, пометки — миграция
/// PhotoEditing), <c>investigation</c> (дела и привязки, ТБ-071) и <c>core</c> (журнал). Изображение строит настоящий
/// Skia; файлы — локальное хранилище ядра во временной папке. Проверяется: копия — отдельный носитель того же дела с
/// грифом источника, перечнем операций и без индексации, оригинал не изменён (ТЭ-007), повтор — дубликат по хешу;
/// пометка берёт гриф носителя и видна только под решёткой; уничтожение источника уносит копии и пометки (ТБ-064),
/// а снимки кадров (ADR-0028) — не трогает.
/// </summary>
/// <remarks>Требуется Docker. Контейнер общий на класс (<see cref="MediaSnapshotFixture"/> — те же три схемы).</remarks>
[Trait("Category", "Gate")]
public sealed class MediaPhotoEditingTests(MediaSnapshotFixture fixture) : IClassFixture<MediaSnapshotFixture>
{
    [Fact(DisplayName = "ADR-0050: копия — новый носитель дела с грифом источника и перечнем операций, не индексируется; оригинал цел; повтор — дубликат; копию не обработать; журнал на копию")]
    public async Task Edited_copy_is_separate_asset_of_the_same_case()
    {
        await InvestigationTestKit.AssignRolesAsync(fixture.Investigation, (30, InvestigationRole.Investigator));
        var cases = InvestigationTestKit.CreateCaseStore(fixture.Investigation, fixture.Core);
        var scope = InvestigationTestKit.CreateCaseScope(fixture.Investigation, fixture.Core);
        var owner = InvestigationTestKit.Access(30, 9, 8);
        var (created, caseId) = await cases.CreateAsync(InvestigationTestKit.Draft("ОБ-1", 8, 2, 30), owner);
        created.ShouldBe(CaseWriteResult.Ok);

        var storage = new LocalFileStorage(Path.Combine(fixture.StorageRoot, "edit-1"));
        var store = new MediaStore(fixture.Media, storage);
        var catalog = new MediaCatalog(fixture.Media, new AllowAllAccessPolicy());
        var originalBytes = Png(40, 20);
        var original = await store.ReceiveAsync(
            new MediaAssetDraft("двор.png", "image/png", MediaKind.Image, 2, 8, "камера", null, 30), new MemoryStream(originalBytes));
        await scope.LinkAssetAsync(caseId, original.AssetId, null, 30);
        var handler = new CreateEditedCopyCommand.Handler(
            AllowUploads(), AccessOf(owner), catalog, scope, store, storage, new SkiaImageTools(), new AuditWriter(fixture.Core));
        var edit = new ImageEdit(Brightness: 10, Rotation: 90);

        var response = await handler.Handle(new CreateEditedCopyCommand(original.AssetId, edit), CancellationToken.None);

        response.Status.ShouldBeTrue(response.StatusMessage);
        var result = response.Data.ShouldNotBeNull();
        result.Duplicate.ShouldBeFalse();
        result.Operations.ShouldBe("поворот 90°; яркость +10");
        var copy = (await catalog.GetAsync(result.AssetId, owner)).ShouldNotBeNull();
        copy.IsEditedCopy.ShouldBeTrue();
        copy.Edit.ShouldBe(edit);
        copy.SourceAssetId.ShouldBe(original.AssetId);
        copy.IndexStatus.ShouldBe(MediaIndexStatus.NotApplicable);
        copy.Classification.ShouldBe<short>(2);
        copy.DivisionId.ShouldBe(8);
        copy.ContentType.ShouldBe("image/png");
        copy.OriginalFileName.ShouldBe("двор (обработка).png");
        copy.Source.ShouldBe("Обработанная копия носителя № " + Id(original.AssetId) + ": поворот 90°; яркость +10");
        copy.UploadedByUserId.ShouldBe(30);
        (await scope.GetCaseIdForAssetAsync(copy.Id, owner)).ShouldBe(caseId);

        // Файл копии — повёрнутое изображение; оригинал — байт в байт прежний, строка не тронута.
        using (var decoded = SKBitmap.Decode(await ReadAsync(storage, copy.StoredFileName, copy.Id)))
        {
            (decoded.Width, decoded.Height).ShouldBe((20, 40));
        }

        (await ReadAsync(storage, (await catalog.GetAsync(original.AssetId, owner))!.StoredFileName, original.AssetId)).ShouldBe(originalBytes);
        (await catalog.GetAsync(original.AssetId, owner))!.IsEditedCopy.ShouldBeFalse();

        // Тот же набор ещё раз — те же байты PNG — дубликат по хешу (ТНД-002), второй строки нет.
        var again = await handler.Handle(new CreateEditedCopyCommand(original.AssetId, edit), CancellationToken.None);
        again.Data.ShouldNotBeNull().Duplicate.ShouldBeTrue();
        again.Data.AssetId.ShouldBe(copy.Id);
        await using (var db = fixture.Media.CreateDbContext())
        {
            (await db.Assets.CountAsync(a => a.SourceAssetId == original.AssetId)).ShouldBe(1);
        }

        // Копию не обрабатывают (обрабатывают источник); недопустимый набор — причина от Skia, носителей не прибавилось.
        (await handler.Handle(new CreateEditedCopyCommand(copy.Id, edit), CancellationToken.None)).StatusMessage
            .ShouldBe(CreateEditedCopyCommand.AlreadyCopyMessage);
        (await handler.Handle(new CreateEditedCopyCommand(original.AssetId, new ImageEdit(Crop: new CropRect(0, 0, 50, 50))), CancellationToken.None))
            .StatusMessage.ShouldBe("Область кадрирования выходит за границы изображения.");

        // Журнал (ТБ-030): поступление на копию — по записи на каждое сохранение, с грифом источника.
        var page = await new AuditReader(fixture.Core).QueryAsync(
            new AuditFilter(Action: AuditAction.Ingest, ObjectRef: "media:asset:" + Id(copy.Id) + ":edited-copy"), owner);
        page.TotalCount.ShouldBe(2);
        page.Rows.ShouldAllBe(r => r.SubjectId == 30 && r.Classification == 2 && r.DivisionId == 8
            && r.PayloadSensitive != null && r.PayloadSensitive.Contains("операции: поворот 90°; яркость +10", StringComparison.Ordinal));
    }

    [Fact(DisplayName = "ADR-0050: пометка — с грифом носителя, только на фото и только под решёткой; уничтожение источника уносит копию и все пометки, снимок кадра остаётся")]
    public async Task Annotations_follow_asset_lattice_and_purge()
    {
        var storage = new LocalFileStorage(Path.Combine(fixture.StorageRoot, "edit-2"));
        var store = new MediaStore(fixture.Media, storage);
        var annotations = new MediaAnnotationStore(fixture.Media, new AllowAllAccessPolicy());
        var owner = InvestigationTestKit.Access(40, 9, 11);
        var otherDivision = InvestigationTestKit.Access(41, 9, 12);
        var lowClearance = InvestigationTestKit.Access(42, 1, 11);

        var photo = await store.ReceiveAsync(new MediaAssetDraft("фото.png", "image/png", MediaKind.Image, 3, 11), new MemoryStream(Png(8, 8)));
        var copy = await store.ReceiveAsync(
            new MediaAssetDraft("фото (обработка).png", "image/png", MediaKind.Image, 3, 11, SourceAssetId: photo.AssetId,
                EditOperations: new ImageEdit(Zoom: 2).ToJson()),
            new MemoryStream(Png(16, 16)));
        var snapshot = await store.ReceiveAsync(
            new MediaAssetDraft("кадр.png", "image/png", MediaKind.Image, 3, 11, SourceAssetId: photo.AssetId, SourceTimestampMs: 0),
            new MemoryStream(Png(9, 9)));
        var video = await store.ReceiveAsync(
            new MediaAssetDraft("clip.mp4", "video/mp4", MediaKind.Video, 3, 11), new MemoryStream([0, 0, 0, 0x18, (byte)'f', (byte)'t', (byte)'y', (byte)'p', 7]));

        // Копия при приёме не ставится в индексацию лиц: статус «неприменимо», как у аудио.
        await using (var db = fixture.Media.CreateDbContext())
        {
            (await db.Assets.SingleAsync(a => a.Id == copy.AssetId)).IndexStatus.ShouldBe(MediaIndexStatus.NotApplicable);
            (await db.Assets.SingleAsync(a => a.Id == snapshot.AssetId)).IndexStatus.ShouldBe(MediaIndexStatus.Uploaded);
        }

        var arrow = new AnnotationDraft(photo.AssetId, AnnotationKind.Arrow, 1, 1, 7, 7, null, "#e53935");
        var first = (await annotations.AddAsync(arrow, 40, owner)).ShouldNotBeNull();
        (await annotations.AddAsync(arrow with { AssetId = copy.AssetId, Kind = AnnotationKind.Text, Text = "  вход  " }, 40, owner)).ShouldNotBeNull();

        // Не фото, чужое подразделение, гриф выше допуска — пометки нет (неотличимо от отсутствующего носителя).
        (await annotations.AddAsync(arrow with { AssetId = video.AssetId }, 40, owner)).ShouldBeNull();
        (await annotations.AddAsync(arrow, 41, otherDivision)).ShouldBeNull();
        (await annotations.AddAsync(arrow, 42, lowClearance)).ShouldBeNull();

        // Строка пометки несёт гриф и подразделение носителя (ТБ-070); цвет — в верхнем регистре, текст — без пробелов по краям.
        await using (var db = fixture.Media.CreateDbContext())
        {
            var row = await db.Annotations.AsNoTracking().SingleAsync(a => a.Id == first);
            row.Classification.ShouldBe<short>(3);
            row.DivisionId.ShouldBe(11);
            row.Color.ShouldBe(AnnotationColors.Red);
            row.AuthorUserId.ShouldBe(40);
            (await db.Annotations.AsNoTracking().SingleAsync(a => a.AssetId == copy.AssetId)).Text.ShouldBe("вход");
        }

        var listed = (await annotations.ListAsync(photo.AssetId, owner)).ShouldHaveSingleItem();
        listed.Kind.ShouldBe(AnnotationKind.Arrow);
        listed.CreatedAt.Kind.ShouldBe(DateTimeKind.Utc);
        (await annotations.ListAsync(photo.AssetId, otherDivision)).ShouldBeEmpty();
        (await annotations.ListAsync(photo.AssetId, lowClearance)).ShouldBeEmpty();
        (await annotations.GetAssetIdAsync(first, owner)).ShouldBe(photo.AssetId);
        (await annotations.GetAssetIdAsync(first, otherDivision)).ShouldBeNull();
        (await annotations.DeleteAsync(first, otherDivision)).ShouldBeFalse();

        // Уничтожение источника (ТБ-064): сначала копия своим актом, затем источник; пометки обоих ушли каскадом;
        // снимок кадра — самостоятельный носитель — и видео целы.
        var purger = new MediaPurger(fixture.Media, new AuditWriter(fixture.Core), storage);
        var purged = await purger.PurgeAsync(photo.AssetId, subjectId: 40);

        purged.Found.ShouldBeTrue();
        await using (var db = fixture.Media.CreateDbContext())
        {
            (await db.Assets.AnyAsync(a => a.Id == photo.AssetId || a.Id == copy.AssetId)).ShouldBeFalse();
            (await db.Annotations.AnyAsync(a => a.AssetId == photo.AssetId || a.AssetId == copy.AssetId)).ShouldBeFalse();
            (await db.Assets.CountAsync(a => a.Id == snapshot.AssetId || a.Id == video.AssetId)).ShouldBe(2);
        }

        await using (var core = fixture.Core.CreateDbContext())
        {
            var acts = await core.AuditRecords.AsNoTracking()
                .Where(r => r.Action == AuditAction.Purge && (r.ObjectRef == "media:asset:" + Id(photo.AssetId) || r.ObjectRef == "media:asset:" + Id(copy.AssetId)))
                .OrderBy(r => r.Id)
                .ToListAsync();
            acts.Select(r => r.ObjectRef).ShouldBe(["media:asset:" + Id(copy.AssetId), "media:asset:" + Id(photo.AssetId)]);
            acts.ShouldAllBe(r => r.Classification == 3 && r.DivisionId == 11 && r.SubjectId == 40);
            acts.ShouldAllBe(r => r.PayloadSensitive != null && r.PayloadSensitive.Contains("пометок 1", StringComparison.Ordinal));
        }
    }

    // ---------- обвязка ----------

    /// <summary>Синтетическое PNG: градиент, чтобы поворот и тон меняли байты (без реальных снимков в тестах).</summary>
    private static byte[] Png(int width, int height)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                bitmap.SetPixel(x, y, new SKColor((byte)(x * 255 / width), (byte)(y * 255 / height), 128));
            }
        }

        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static string Id(int id) => id.ToString(CultureInfo.InvariantCulture);

    private static IMediaAdministration AllowUploads()
    {
        var administration = Substitute.For<IMediaAdministration>();
        administration.CanUploadAsync(Arg.Any<CancellationToken>()).Returns(true);
        return administration;
    }

    private static IAccessContextProvider AccessOf(AccessContext access)
    {
        var provider = Substitute.For<IAccessContextProvider>();
        provider.GetCurrentAsync(Arg.Any<CancellationToken>()).Returns(access);
        return provider;
    }

    private static async Task<byte[]> ReadAsync(LocalFileStorage storage, string storedFileName, int assetId)
    {
        await using var stream = await storage.OpenReadAsync(storedFileName, MediaFileCategories.Originals, Id(assetId));
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer);
        return buffer.ToArray();
    }
}
