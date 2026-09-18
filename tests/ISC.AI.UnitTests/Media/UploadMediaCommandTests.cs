using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.BackgroundTasks;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.Media.Application.Features.Assets;
using ISC.AI.Modules.Media.Domain.Model;
using ISC.AI.Modules.Media.Domain.Services;
using NSubstitute;
using Shouldly;
using Xunit;

namespace ISC.AI.UnitTests.Media;

/// <summary>Загрузка носителя в дело (ТФ-МЕД-01, ТБ-070): allowlist форматов, гриф от дела, индексация только для нового файла.</summary>
public sealed class UploadMediaCommandTests
{
    /// <summary>JPEG: сигнатура FF D8 FF.</summary>
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01];

    /// <summary>MP4: «ftyp» по смещению 4.</summary>
    private static readonly byte[] Mp4 = [0x00, 0x00, 0x00, 0x18, (byte)'f', (byte)'t', (byte)'y', (byte)'p', (byte)'i', (byte)'s', (byte)'o', (byte)'m'];

    private readonly IMediaAdministration _administration = Substitute.For<IMediaAdministration>();
    private readonly IAccessContextProvider _accessProvider = Substitute.For<IAccessContextProvider>();
    private readonly ICaseScope _caseScope = Substitute.For<ICaseScope>();
    private readonly IMediaStore _store = Substitute.For<IMediaStore>();
    private readonly RecordingQueue _queue = new();

    public UploadMediaCommandTests()
    {
        _administration.CanUploadAsync(Arg.Any<CancellationToken>()).Returns(true);
        _accessProvider.GetCurrentAsync(Arg.Any<CancellationToken>()).Returns(new AccessContext("7", 2, [1]));
        _caseScope.GetCaseAsync(3, Arg.Any<AccessContext>(), Arg.Any<CancellationToken>())
            .Returns(new CaseScopeItem(3, "№ 1", "Дело", Classification: 2, DivisionId: 1));
    }

    [Theory(DisplayName = "Валидатор: allowlist форматов — изображения и видео проходят, документы и пустые файлы — нет")]
    [InlineData("image/jpeg", 10, true)]
    [InlineData("video/mp4", 10, true)]
    [InlineData("application/pdf", 10, false)]
    [InlineData("image/jpeg", 0, false)]
    public void Validator_enforces_allowlist_and_size(string contentType, int size, bool expected)
    {
        var result = new UploadMediaValidator().Validate(new UploadMediaCommand(3, "a.bin", contentType, new byte[size]));
        result.IsValid.ShouldBe(expected);
    }

    [Fact(DisplayName = "Недоступное дело → NotFound, файл не принимается, индексация не ставится")]
    public async Task Inaccessible_case_is_not_found()
    {
        var response = await HandleAsync(new UploadMediaCommand(99, "a.jpg", "image/jpeg", Jpeg));

        response.StatusCode.ShouldBe(ResponseStatusCode.NotFound);
        await _store.DidNotReceive().ReceiveAsync(Arg.Any<MediaAssetDraft>(), Arg.Any<Stream>(), Arg.Any<CancellationToken>());
        _queue.Kinds.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Дубликат по хешу: привязка к делу есть, повторная индексация не ставится")]
    public async Task Duplicate_is_linked_but_not_reindexed()
    {
        _store.ReceiveAsync(Arg.Any<MediaAssetDraft>(), Arg.Any<Stream>(), Arg.Any<CancellationToken>())
            .Returns(new MediaAssetReceipt(50, Duplicate: true));

        var response = await HandleAsync(new UploadMediaCommand(3, "a.jpg", "image/jpeg", Jpeg, Place: "камера 2"));

        response.Status.ShouldBeTrue();
        response.Data!.Duplicate.ShouldBeTrue();
        await _caseScope.Received(1).LinkAssetAsync(3, 50, "камера 2", 7, Arg.Any<CancellationToken>());
        _queue.Kinds.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Успех: гриф/подразделение — от дела (ТБ-070), носитель привязан, индексация поставлена в очередь")]
    public async Task Success_links_and_enqueues_indexing()
    {
        MediaAssetDraft? draft = null;
        _store.ReceiveAsync(Arg.Do<MediaAssetDraft>(d => draft = d), Arg.Any<Stream>(), Arg.Any<CancellationToken>())
            .Returns(new MediaAssetReceipt(51, Duplicate: false));

        var response = await HandleAsync(new UploadMediaCommand(3, "clip.mp4", "video/mp4", Mp4, Source: "камера"));

        response.Status.ShouldBeTrue();
        response.Data!.AssetId.ShouldBe(51);
        draft.ShouldNotBeNull();
        draft.Kind.ShouldBe(MediaKind.Video);
        draft.Classification.ShouldBe((short)2);
        draft.DivisionId.ShouldBe(1);
        draft.UploadedByUserId.ShouldBe(7);
        await _caseScope.Received(1).LinkAssetAsync(3, 51, null, 7, Arg.Any<CancellationToken>());
        _queue.Kinds.Count.ShouldBe(1);
    }

    [Fact(DisplayName = "Без права загрузки → BadRequest до обращения к делу")]
    public async Task Without_upload_right_is_bad_request()
    {
        _administration.CanUploadAsync(Arg.Any<CancellationToken>()).Returns(false);

        var response = await HandleAsync(new UploadMediaCommand(3, "a.jpg", "image/jpeg", Jpeg));

        response.StatusCode.ShouldBe(ResponseStatusCode.BadRequest);
        await _caseScope.DidNotReceive().GetCaseAsync(Arg.Any<int>(), Arg.Any<AccessContext>(), Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "Подмена типа (ТС-010): заявлено изображение, а содержимое — видео (и наоборот) → BadRequest, файл не принимается")]
    public async Task Content_family_mismatch_is_bad_request()
    {
        var imageDeclared = await HandleAsync(new UploadMediaCommand(3, "a.jpg", "image/jpeg", Mp4));
        imageDeclared.StatusCode.ShouldBe(ResponseStatusCode.BadRequest);
        imageDeclared.StatusMessage.ShouldContain("не соответствует заявленному типу");

        var videoDeclared = await HandleAsync(new UploadMediaCommand(3, "clip.mp4", "video/mp4", Jpeg));
        videoDeclared.StatusCode.ShouldBe(ResponseStatusCode.BadRequest);

        var unknown = await HandleAsync(new UploadMediaCommand(3, "a.jpg", "image/jpeg", [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12]));
        unknown.StatusCode.ShouldBe(ResponseStatusCode.BadRequest);
        unknown.StatusMessage.ShouldContain("не распознано");

        await _store.DidNotReceive().ReceiveAsync(Arg.Any<MediaAssetDraft>(), Arg.Any<Stream>(), Arg.Any<CancellationToken>());
        await _caseScope.DidNotReceive().LinkAssetAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<int?>(), Arg.Any<CancellationToken>());
        _queue.Kinds.ShouldBeEmpty();
    }

    [Fact(DisplayName = "TIFF и HEIC не принимаются: конвейер их не декодирует (валидатор и allowlist)")]
    public void Tiff_and_heic_are_rejected()
    {
        MediaFileRules.IsAllowed("image/tiff").ShouldBeFalse();
        MediaFileRules.IsAllowed("image/heic").ShouldBeFalse();
        new UploadMediaValidator().Validate(new UploadMediaCommand(3, "a.tiff", "image/tiff", Jpeg)).IsValid.ShouldBeFalse();
    }

    [Fact(DisplayName = "Валидатор: пределы длины «Источник»/«Место» — константы для интерфейса")]
    public void Validator_limits_source_and_place_length()
    {
        var validator = new UploadMediaValidator();
        validator.Validate(new UploadMediaCommand(3, "a.jpg", "image/jpeg", Jpeg, Source: new string('и', UploadMediaValidator.MaxSourceLength))).IsValid.ShouldBeTrue();
        validator.Validate(new UploadMediaCommand(3, "a.jpg", "image/jpeg", Jpeg, Source: new string('и', UploadMediaValidator.MaxSourceLength + 1))).IsValid.ShouldBeFalse();
        validator.Validate(new UploadMediaCommand(3, "a.jpg", "image/jpeg", Jpeg, Place: new string('м', UploadMediaValidator.MaxPlaceLength + 1))).IsValid.ShouldBeFalse();
    }

    [Theory(DisplayName = "Сниффер сигнатур: семейство по первым байтам, неизвестное — null")]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xDB }, MediaKind.Image)]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, MediaKind.Image)]
    [InlineData(new byte[] { 0x42, 0x4D, 0x00, 0x00 }, MediaKind.Image)]
    [InlineData(new byte[] { 0x47, 0x49, 0x46, 0x38, 0x39, 0x61 }, MediaKind.Image)]
    [InlineData(new byte[] { 0x52, 0x49, 0x46, 0x46, 0, 0, 0, 0, 0x57, 0x45, 0x42, 0x50 }, MediaKind.Image)]
    [InlineData(new byte[] { 0x52, 0x49, 0x46, 0x46, 0, 0, 0, 0, 0x41, 0x56, 0x49, 0x20 }, MediaKind.Video)]
    [InlineData(new byte[] { 0x1A, 0x45, 0xDF, 0xA3, 0, 0 }, MediaKind.Video)]
    [InlineData(new byte[] { 0, 0, 0, 0x14, 0x66, 0x74, 0x79, 0x70, 0x71, 0x74, 0x20, 0x20 }, MediaKind.Video)]
    [InlineData(new byte[] { 0x25, 0x50, 0x44, 0x46, 0x2D, 0x31, 0x2E, 0x34, 0, 0, 0, 0 }, null)]
    [InlineData(new byte[] { 0x52, 0x49, 0x46, 0x46 }, null)]
    [InlineData(new byte[0], null)]
    public void Sniffer_detects_family_by_signature(byte[] content, MediaKind? expected)
    {
        ContentSniffer.Sniff(content).ShouldBe(expected);
    }

    private async Task<ResponseDto<MediaAssetReceipt>> HandleAsync(UploadMediaCommand command)
    {
        var handler = new UploadMediaCommand.Handler(_administration, _accessProvider, _caseScope, _store, _queue);
        return await handler.Handle(command, CancellationToken.None);
    }

    /// <summary>Очередь-регистратор: запоминает виды поставленных задач, не исполняя их.</summary>
    private sealed class RecordingQueue : IBackgroundTaskQueue
    {
        public List<string> Kinds { get; } = [];

        public ValueTask<Guid> EnqueueAsync(
            string kind, Func<IServiceProvider, CancellationToken, Task> work, CancellationToken cancellationToken = default)
        {
            Kinds.Add(kind);
            return ValueTask.FromResult(Guid.NewGuid());
        }
    }
}
