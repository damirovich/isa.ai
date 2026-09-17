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
        var response = await HandleAsync(new UploadMediaCommand(99, "a.jpg", "image/jpeg", [1, 2, 3]));

        response.StatusCode.ShouldBe(ResponseStatusCode.NotFound);
        await _store.DidNotReceive().ReceiveAsync(Arg.Any<MediaAssetDraft>(), Arg.Any<Stream>(), Arg.Any<CancellationToken>());
        _queue.Kinds.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Дубликат по хешу: привязка к делу есть, повторная индексация не ставится")]
    public async Task Duplicate_is_linked_but_not_reindexed()
    {
        _store.ReceiveAsync(Arg.Any<MediaAssetDraft>(), Arg.Any<Stream>(), Arg.Any<CancellationToken>())
            .Returns(new MediaAssetReceipt(50, Duplicate: true));

        var response = await HandleAsync(new UploadMediaCommand(3, "a.jpg", "image/jpeg", [1, 2, 3], Place: "камера 2"));

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

        var response = await HandleAsync(new UploadMediaCommand(3, "clip.mp4", "video/mp4", [1, 2, 3], Source: "камера"));

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

        var response = await HandleAsync(new UploadMediaCommand(3, "a.jpg", "image/jpeg", [1]));

        response.StatusCode.ShouldBe(ResponseStatusCode.BadRequest);
        await _caseScope.DidNotReceive().GetCaseAsync(Arg.Any<int>(), Arg.Any<AccessContext>(), Arg.Any<CancellationToken>());
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
