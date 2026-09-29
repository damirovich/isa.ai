using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.Media.Application.Features.Assets;
using ISC.AI.Modules.Media.Domain.Services;
using NSubstitute;
using Shouldly;
using Xunit;

namespace ISC.AI.UnitTests.Media;

/// <summary>
/// Обязательная дата и время съёмки (ТФ-МЕД-17): дата из метаданных файла (EXIF фото, заголовок MP4/MOV) как
/// значение по умолчанию; неправдоподобное и отсутствующее — «даты нет»; у фото и видео без даты загрузка
/// отклоняется, у аудио — нет; правка даты у носителя — только в делах субъекта и по праву загрузки.
/// </summary>
public sealed class CaptureTimeTests
{
    private static readonly TimeZoneInfo Bishkek = TimeZoneInfo.CreateCustomTimeZone("KGT", TimeSpan.FromHours(6), "KGT", "KGT");
    private static readonly DateTimeOffset Now = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);

    [Theory(DisplayName = "EXIF JPEG: DateTimeOriginal без смещения — время местное оператора; порядок байт II и MM")]
    [InlineData(true)]
    [InlineData(false)]
    public void Exif_date_is_read_in_local_zone(bool littleEndian)
    {
        var jpeg = Jpeg(littleEndian, original: "2026:09:28 14:30:05", offset: null);

        var found = CaptureTimeProbe.TryRead(jpeg, Bishkek, Now).ShouldNotBeNull();

        found.ShouldBe(new DateTimeOffset(2026, 9, 28, 14, 30, 5, TimeSpan.FromHours(6)));
    }

    [Fact(DisplayName = "EXIF JPEG: смещение камеры (OffsetTimeOriginal) точнее пояса оператора")]
    public void Exif_offset_wins_over_local_zone()
    {
        var jpeg = Jpeg(true, original: "2026:09:28 14:30:05", offset: "+03:00");

        CaptureTimeProbe.TryRead(jpeg, Bishkek, Now).ShouldBe(new DateTimeOffset(2026, 9, 28, 14, 30, 5, TimeSpan.FromHours(3)));
    }

    [Fact(DisplayName = "MP4: mvhd.creation_time (секунды от 1904 года, UTC)")]
    public void Mp4_creation_time_is_utc()
    {
        var expected = new DateTimeOffset(2026, 9, 28, 8, 15, 0, TimeSpan.Zero);
        var seconds = (uint)(expected - new DateTimeOffset(1904, 1, 1, 0, 0, 0, TimeSpan.Zero)).TotalSeconds;

        CaptureTimeProbe.TryRead(Mp4(seconds), Bishkek, Now).ShouldBe(expected);
        CaptureTimeProbe.TryRead(Mp4(0), Bishkek, Now).ShouldBeNull(); // «не задано»
    }

    [Fact(DisplayName = "Нет метаданных, мусор, дата из будущего или до 1990 года — «даты нет», а не ошибка")]
    public void Implausible_or_missing_is_null()
    {
        CaptureTimeProbe.TryRead([0xFF, 0xD8, 0xFF, 0xD9, 0, 0], Bishkek, Now).ShouldBeNull();
        CaptureTimeProbe.TryRead(Encoding.ASCII.GetBytes("просто текст, не медиа"), Bishkek, Now).ShouldBeNull();
        CaptureTimeProbe.TryRead(Jpeg(true, "2027:01:01 00:00:00", null), Bishkek, Now).ShouldBeNull();
        CaptureTimeProbe.TryRead(Jpeg(true, "1985:01:01 00:00:00", null), Bishkek, Now).ShouldBeNull();
        CaptureTimeProbe.TryRead(Jpeg(true, "не дата", null), Bishkek, Now).ShouldBeNull();

        // Битое смещение в EXIF не роняет чтение.
        var broken = Jpeg(true, "2026:09:28 14:30:05", null);
        broken[^4] = 0xFF;
        Should.NotThrow(() => CaptureTimeProbe.TryRead(broken, Bishkek, Now));
    }

    [Fact(DisplayName = "Загрузка: фото и видео без даты съёмки отклоняются, аудио — нет; дата из будущего — ошибка")]
    public void Upload_requires_capture_time_for_photo_and_video()
    {
        var validator = new UploadMediaValidator();
        var content = new byte[] { 1, 2, 3 };

        validator.Validate(new UploadMediaCommand(1, "a.jpg", "image/jpeg", content)).IsValid.ShouldBeFalse();
        validator.Validate(new UploadMediaCommand(1, "a.mp4", "video/mp4", content)).IsValid.ShouldBeFalse();
        validator.Validate(new UploadMediaCommand(1, "a.mp3", "audio/mpeg", content)).IsValid.ShouldBeTrue();
        validator.Validate(new UploadMediaCommand(1, "a.jpg", "image/jpeg", content, CapturedAt: DateTimeOffset.UtcNow.AddHours(-1)))
            .IsValid.ShouldBeTrue();
        validator.Validate(new UploadMediaCommand(1, "a.jpg", "image/jpeg", content, CapturedAt: DateTimeOffset.UtcNow.AddDays(3)))
            .IsValid.ShouldBeFalse();
    }

    [Fact(DisplayName = "Правка даты у носителя: без права — отказ; носитель вне дел субъекта — «не найден»; иначе записывается")]
    public async Task Set_captured_at_checks_rights_and_scope()
    {
        var administration = Substitute.For<IMediaAdministration>();
        var access = Substitute.For<IAccessContextProvider>();
        var scope = Substitute.For<ICaseScope>();
        var store = Substitute.For<IMediaStore>();
        access.GetCurrentAsync(Arg.Any<CancellationToken>()).Returns(new AccessContext("5", 2, [1]));
        var handler = new SetAssetCapturedAtCommand.Handler(administration, access, scope, store);
        var command = new SetAssetCapturedAtCommand(7, new DateTimeOffset(2026, 9, 28, 14, 0, 0, TimeSpan.FromHours(6)));

        (await handler.Handle(command, CancellationToken.None)).StatusCode.ShouldBe(ResponseStatusCode.BadRequest);

        administration.CanUploadAsync(Arg.Any<CancellationToken>()).Returns(true);
        (await handler.Handle(command, CancellationToken.None)).StatusCode.ShouldBe(ResponseStatusCode.NotFound);
        await store.DidNotReceiveWithAnyArgs().SetCapturedAtAsync(default, default, default);

        scope.IsAssetAccessibleAsync(7, Arg.Any<AccessContext>(), Arg.Any<CancellationToken>()).Returns(true);
        store.SetCapturedAtAsync(7, command.CapturedAt, Arg.Any<CancellationToken>()).Returns(true);
        (await handler.Handle(command, CancellationToken.None)).Status.ShouldBeTrue();
        command.AuditSummary.ShouldBe("media:asset:7:captured-at:2026-09-28T08:00:00Z");
    }

    // --- сборка тестовых файлов ---

    private static byte[] Jpeg(bool little, string original, string? offset)
    {
        // TIFF: заголовок (8) → IFD0 с одной записью ExifIFD → Exif IFD с DateTimeOriginal (+ OffsetTimeOriginal) → строки.
        var exifEntries = offset is null ? 1 : 2;
        const int ifd0 = 8;
        const int ifd0Size = 2 + 12 + 4;
        var exifIfd = ifd0 + ifd0Size;
        var exifSize = 2 + (12 * exifEntries) + 4;
        var dataAt = exifIfd + exifSize;
        var originalBytes = Encoding.ASCII.GetBytes(original + "\0");
        var offsetBytes = offset is null ? [] : Encoding.ASCII.GetBytes(offset + "\0");

        var tiff = new byte[dataAt + originalBytes.Length + offsetBytes.Length];
        tiff[0] = tiff[1] = (byte)(little ? 'I' : 'M');
        U16(tiff, 2, 42, little);
        U32(tiff, 4, ifd0, little);

        U16(tiff, ifd0, 1, little);
        Entry(tiff, ifd0 + 2, 0x8769, 4, 1, (uint)exifIfd, little);

        U16(tiff, exifIfd, (ushort)exifEntries, little);
        Entry(tiff, exifIfd + 2, 0x9003, 2, (uint)originalBytes.Length, (uint)dataAt, little);
        originalBytes.CopyTo(tiff, dataAt);
        if (offset is not null)
        {
            Entry(tiff, exifIfd + 14, 0x9011, 2, (uint)offsetBytes.Length, (uint)(dataAt + originalBytes.Length), little);
            offsetBytes.CopyTo(tiff, dataAt + originalBytes.Length);
        }

        var app1 = new List<byte>();
        app1.AddRange("Exif\0\0"u8.ToArray());
        app1.AddRange(tiff);

        var jpeg = new List<byte> { 0xFF, 0xD8, 0xFF, 0xE1 };
        var length = (ushort)(app1.Count + 2);
        jpeg.Add((byte)(length >> 8));
        jpeg.Add((byte)length);
        jpeg.AddRange(app1);
        jpeg.AddRange(new byte[] { 0xFF, 0xD9 });
        return jpeg.ToArray();
    }

    private static byte[] Mp4(uint creationSeconds)
    {
        var ftyp = Box("ftyp", Encoding.ASCII.GetBytes("isom\0\0\0\0isom"));
        var mvhdBody = new byte[100];
        BinaryPrimitives.WriteUInt32BigEndian(mvhdBody.AsSpan(4, 4), creationSeconds);
        var moov = Box("moov", Box("mvhd", mvhdBody));
        var file = new byte[ftyp.Length + moov.Length];
        ftyp.CopyTo(file, 0);
        moov.CopyTo(file, ftyp.Length);
        return file;
    }

    private static byte[] Box(string type, byte[] body)
    {
        var box = new byte[8 + body.Length];
        BinaryPrimitives.WriteUInt32BigEndian(box.AsSpan(0, 4), (uint)box.Length);
        Encoding.ASCII.GetBytes(type).CopyTo(box, 4);
        body.CopyTo(box, 8);
        return box;
    }

    private static void Entry(byte[] data, int at, ushort tag, ushort type, uint count, uint value, bool little)
    {
        U16(data, at, tag, little);
        U16(data, at + 2, type, little);
        U32(data, at + 4, count, little);
        U32(data, at + 8, value, little);
    }

    private static void U16(byte[] data, int at, ushort value, bool little)
    {
        if (little) { BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(at, 2), value); }
        else { BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(at, 2), value); }
    }

    private static void U32(byte[] data, int at, uint value, bool little)
    {
        if (little) { BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(at, 4), value); }
        else { BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(at, 4), value); }
    }
}
