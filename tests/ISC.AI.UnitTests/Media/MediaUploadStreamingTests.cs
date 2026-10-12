using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.BackgroundTasks;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.Media.Application;
using ISC.AI.Modules.Media.Application.Features.Assets;
using ISC.AI.Modules.Media.Domain.Model;
using ISC.AI.Modules.Media.Domain.Services;
using ISC.AI.Modules.Media.UI;
using Microsoft.Extensions.Configuration;
using NSubstitute;
using Shouldly;
using Xunit;

namespace ISC.AI.UnitTests.Media;

/// <summary>
/// Приём больших файлов потоком (ADR-0040, ТФ-МЕД-01): предел по виду носителя (фото — 200 МБ, видео и аудио — из
/// настройки), заявленная длина отсекает заведомо большой файл до чтения, фактическая — при чтении; поток доходит до
/// хранилища побайтно, вместе с заголовком, прочитанным для проверки сигнатуры.
/// </summary>
public sealed class MediaUploadStreamingTests
{
    private const long Gigabyte = 1024L * 1024 * 1024;

    /// <summary>Начало MP4: «ftyp» по смещению 4.</summary>
    private static readonly byte[] Mp4Header = [0x00, 0x00, 0x00, 0x18, (byte)'f', (byte)'t', (byte)'y', (byte)'p', (byte)'i', (byte)'s', (byte)'o', (byte)'m'];

    private readonly IMediaAdministration _administration = Substitute.For<IMediaAdministration>();
    private readonly IAccessContextProvider _accessProvider = Substitute.For<IAccessContextProvider>();
    private readonly ICaseScope _caseScope = Substitute.For<ICaseScope>();
    private readonly IMediaStore _store = Substitute.For<IMediaStore>();
    private readonly IBackgroundTaskQueue _queue = Substitute.For<IBackgroundTaskQueue>();

    public MediaUploadStreamingTests()
    {
        _administration.CanUploadAsync(Arg.Any<CancellationToken>()).Returns(true);
        _accessProvider.GetCurrentAsync(Arg.Any<CancellationToken>()).Returns(new AccessContext("7", 2, [1]));
        _caseScope.GetCaseAsync(3, Arg.Any<AccessContext>(), Arg.Any<CancellationToken>())
            .Returns(new CaseScopeItem(3, "№ 1", "Дело", Classification: 2, DivisionId: 1));
    }

    [Fact(DisplayName = "Настройка предела: по умолчанию 16 ГБ; мусор — умолчание; меньше 200 МБ и больше 64 ГБ — к границе")]
    public void Options_are_read_and_clamped()
    {
        static MediaUploadOptions Read(string? value) => MediaUploadOptions.Read(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [MediaUploadOptions.MaxStreamedFileBytesKey] = value })
            .Build());

        Read(null).MaxStreamedFileBytes.ShouldBe(16 * Gigabyte);
        Read("мусор").MaxStreamedFileBytes.ShouldBe(16 * Gigabyte);
        Read((4 * Gigabyte).ToString(System.Globalization.CultureInfo.InvariantCulture)).MaxStreamedFileBytes.ShouldBe(4 * Gigabyte);
        Read("1024").MaxStreamedFileBytes.ShouldBe(MediaFileRules.MaxImageBytes);
        Read((100 * Gigabyte).ToString(System.Globalization.CultureInfo.InvariantCulture)).MaxStreamedFileBytes.ShouldBe(64 * Gigabyte);

        var options = Read((4 * Gigabyte).ToString(System.Globalization.CultureInfo.InvariantCulture));
        options.MaxBytesFor(MediaKind.Image).ShouldBe(200L * 1024 * 1024);
        options.MaxBytesFor(MediaKind.Video).ShouldBe(4 * Gigabyte);
        options.MaxBytesFor(MediaKind.Audio).ShouldBe(4 * Gigabyte);
    }

    [Fact(DisplayName = "Валидатор: видео 5 ГБ проходит; фото 300 МБ и видео 17 ГБ — «больше допустимых 200 МБ / 16 ГБ»")]
    public void Validator_applies_limit_by_kind()
    {
        var validator = new UploadMediaValidator();

        validator.Validate(Command("video/mp4", 5 * Gigabyte)).IsValid.ShouldBeTrue();

        var photo = validator.Validate(Command("image/jpeg", 300L * 1024 * 1024));
        photo.IsValid.ShouldBeFalse();
        photo.Errors.ShouldContain(e => e.ErrorMessage == "Файл больше допустимых 200 МБ.");

        var video = validator.Validate(Command("video/mp4", 17 * Gigabyte));
        video.IsValid.ShouldBeFalse();
        video.Errors.ShouldContain(e => e.ErrorMessage == "Файл больше допустимых 16 ГБ.");

        var narrowed = new UploadMediaValidator(new MediaUploadOptions(Gigabyte));
        narrowed.Validate(Command("audio/ogg", 2 * Gigabyte)).IsValid.ShouldBeFalse();
        narrowed.Validate(Command("audio/ogg", Gigabyte)).IsValid.ShouldBeTrue();
        narrowed.Validate(Command("video/mp4", 0)).Errors.ShouldContain(e => e.ErrorMessage == "Файл пуст.");
    }

    [Fact(DisplayName = "Поток доходит до хранилища побайтно: заголовок, прочитанный для сигнатуры, не теряется и не дублируется")]
    public async Task Stream_reaches_store_byte_for_byte()
    {
        var content = Mp4Header.Concat(Enumerable.Range(0, 3 * UploadMediaCommand.HeaderBytes).Select(i => (byte)(i % 251))).ToArray();
        byte[]? stored = null;
        _store.ReceiveAsync(Arg.Any<MediaAssetDraft>(), Arg.Any<Stream>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                using var copy = new MemoryStream();
                await call.ArgAt<Stream>(1).CopyToAsync(copy);
                stored = copy.ToArray();
                return new MediaAssetReceipt(40, Duplicate: false);
            });

        var response = await HandleAsync(
            Command("video/mp4", content.Length, new OneByteChunksStream(content)), MediaUploadOptions.Default);

        response.Status.ShouldBeTrue();
        stored.ShouldBe(content);
    }

    [Fact(DisplayName = "Источник прислал больше заявленного и больше предела → «больше допустимых», в дело ничего не попадает")]
    public async Task Actual_size_over_limit_is_rejected_while_reading()
    {
        var content = Mp4Header.Concat(new byte[20 * 1024]).ToArray();
        _store.ReceiveAsync(Arg.Any<MediaAssetDraft>(), Arg.Any<Stream>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                await call.ArgAt<Stream>(1).CopyToAsync(Stream.Null);
                return new MediaAssetReceipt(41, Duplicate: false);
            });

        // Заявлено 1000 байт — в пределе 8 КБ, на деле 20 КБ.
        var response = await HandleAsync(
            Command("video/mp4", 1000, new MemoryStream(content)), new MediaUploadOptions(8 * 1024));

        response.StatusCode.ShouldBe(ResponseStatusCode.BadRequest);
        response.StatusMessage.ShouldBe($"Файл больше допустимых {MediaFileRules.DescribeSize(8 * 1024)}.");
        await _caseScope.DidNotReceive().LinkAssetAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<int?>(), Arg.Any<CancellationToken>());
        await _queue.DidNotReceive().EnqueueAsync(Arg.Any<string>(), Arg.Any<Func<IServiceProvider, CancellationToken, Task>>(), Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "Заявлено больше предела → отказ ДО чтения: поток не трогается")]
    public async Task Declared_size_over_limit_is_rejected_before_reading()
    {
        var response = await HandleAsync(
            Command("video/mp4", 2 * Gigabyte, new UnreadableStream()), new MediaUploadOptions(Gigabyte));

        response.StatusCode.ShouldBe(ResponseStatusCode.BadRequest);
        response.StatusMessage.ShouldBe("Файл больше допустимых 1 ГБ.");
        await _store.DidNotReceive().ReceiveAsync(Arg.Any<MediaAssetDraft>(), Arg.Any<Stream>(), Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "Содержимое загрузки одноразовое: второй раз тот же поток не читается")]
    public async Task Upload_content_is_single_use()
    {
        _store.ReceiveAsync(Arg.Any<MediaAssetDraft>(), Arg.Any<Stream>(), Arg.Any<CancellationToken>())
            .Returns(new MediaAssetReceipt(42, Duplicate: false));
        var command = Command("video/mp4", Mp4Header.Length, new MemoryStream(Mp4Header));

        (await HandleAsync(command, MediaUploadOptions.Default)).Status.ShouldBeTrue();
        await Should.ThrowAsync<InvalidOperationException>(async () => await HandleAsync(command, MediaUploadOptions.Default));
    }

    [Theory(DisplayName = "Размер для человека: 200 МБ, 16 ГБ, 1,5 ГБ")]
    [InlineData(200L * 1024 * 1024, "200 МБ")]
    [InlineData(16L * 1024 * 1024 * 1024, "16 ГБ")]
    [InlineData(3L * 512 * 1024 * 1024, "1,5 ГБ")]
    public void Size_is_described_for_people(long bytes, string expected) =>
        MediaFileRules.DescribeSize(bytes).ShouldBe(expected);

    [Fact(DisplayName = "Ход загрузки на экране: обёртка отдаёт данные без изменений и сообщает накопленный объём")]
    public async Task Progress_stream_reports_running_total()
    {
        var content = Enumerable.Range(0, 10_000).Select(i => (byte)i).ToArray();
        var reported = new List<long>();
        await using var stream = new ProgressReadStream(new OneByteChunksStream(content, chunk: 3000), reported.Add);

        using var copy = new MemoryStream();
        await stream.CopyToAsync(copy);

        copy.ToArray().ShouldBe(content);
        reported.ShouldBe([3000L, 6000L, 9000L, 10_000L]);
    }

    private static UploadMediaCommand Command(string contentType, long length, Stream? stream = null) =>
        new(3, "file", contentType, MediaUploadContent.FromStream(stream ?? Stream.Null, length),
            CapturedAt: DateTimeOffset.UtcNow.AddHours(-1));

    private async Task<ResponseDto<MediaAssetReceipt>> HandleAsync(UploadMediaCommand command, MediaUploadOptions options)
    {
        var handler = new UploadMediaCommand.Handler(_administration, _accessProvider, _caseScope, _store, _queue, options);
        return await handler.Handle(command, CancellationToken.None);
    }

    /// <summary>Неперематываемый поток, отдающий данные маленькими порциями — как поток из браузера.</summary>
    private sealed class OneByteChunksStream(byte[] content, int chunk = 1000) : Stream
    {
        private int _position;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var take = Math.Min(Math.Min(count, chunk), content.Length - _position);
            Array.Copy(content, _position, buffer, offset, take);
            _position += take;
            return take;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>Поток, чтение из которого — ошибка теста.</summary>
    private sealed class UnreadableStream : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => 0;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new InvalidOperationException("Поток не должен читаться: файл отклонён по заявленному размеру.");

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
