using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.BackgroundTasks;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.Media.Application.Features.Assets;
using ISC.AI.Modules.Media.Application.Features.Transcripts;
using ISC.AI.Modules.Media.Domain.Model;
using ISC.AI.Modules.Media.Domain.Services;
using NSubstitute;
using Shouldly;
using Xunit;

namespace ISC.AI.UnitTests.Media;

/// <summary>
/// Сценарии расшифровки речи (ADR-0026): чтение расшифровки (решётка + сужение по делам, ТБ-020/071), поиск по
/// словам в материалах дела (дело доступно, область — носители дела, аудит с искомым текстом, ТБ-030),
/// повторная расшифровка (право, решётка, неприменимость к изображению, очередь) и отказ переиндексации аудио.
/// </summary>
public sealed class MediaTranscriptScenarioTests
{
    private static readonly AccessContext Access = new("7", 2, [1]);

    private readonly IMediaAdministration _administration = Substitute.For<IMediaAdministration>();
    private readonly IAccessContextProvider _accessProvider = Substitute.For<IAccessContextProvider>();
    private readonly ICaseScope _caseScope = Substitute.For<ICaseScope>();
    private readonly IMediaCatalog _catalog = Substitute.For<IMediaCatalog>();
    private readonly IMediaStore _store = Substitute.For<IMediaStore>();
    private readonly RecordingQueue _queue = new();

    public MediaTranscriptScenarioTests()
    {
        _administration.CanUploadAsync(Arg.Any<CancellationToken>()).Returns(true);
        _accessProvider.GetCurrentAsync(Arg.Any<CancellationToken>()).Returns(Access);
        _caseScope.GetCaseAsync(3, Arg.Any<AccessContext>(), Arg.Any<CancellationToken>())
            .Returns(new CaseScopeItem(3, "№ 1", "Дело", Classification: 2, DivisionId: 1));
        _caseScope.GetAssetIdsAsync(Arg.Is<IReadOnlyCollection<int>>(ids => ids.Count == 1 && ids.Contains(3)), Arg.Any<CancellationToken>())
            .Returns([50, 51]);
        _caseScope.IsAssetAccessibleAsync(50, Arg.Any<AccessContext>(), Arg.Any<CancellationToken>()).Returns(true);
        _catalog.GetAsync(50, Arg.Any<AccessContext>(), Arg.Any<CancellationToken>()).Returns(Asset(50, MediaKind.Audio));
        _catalog.GetTranscriptAsync(50, Arg.Any<AccessContext>(), Arg.Any<CancellationToken>())
            .Returns(new MediaTranscript(50, TranscriptStatus.Done, null, "gigaam", DateTime.UtcNow,
                [new TranscriptSegmentRow(0, 0, 1500, "салам")]));

        // По умолчанию расшифровка не в очереди и не идёт — условная постановка проходит.
        _store.TryMarkTranscriptionPendingAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(true);
    }

    // ---------- GetTranscriptQuery ----------

    [Fact(DisplayName = "Чтение расшифровки: носитель дел субъекта и в допуске → фрагменты по порядку")]
    public async Task Get_transcript_returns_segments()
    {
        var response = await GetAsync(new GetTranscriptQuery(50));

        response.Status.ShouldBeTrue();
        response.Data!.Segments.ShouldHaveSingleItem().Text.ShouldBe("салам");
        await _catalog.Received(1).GetTranscriptAsync(50, Access, Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "ТБ-071: носитель чужого дела → NotFound, фрагменты даже не читаются")]
    public async Task Get_transcript_of_foreign_case_asset_is_not_found()
    {
        _caseScope.IsAssetAccessibleAsync(50, Arg.Any<AccessContext>(), Arg.Any<CancellationToken>()).Returns(false);

        var response = await GetAsync(new GetTranscriptQuery(50));

        response.StatusCode.ShouldBe(ResponseStatusCode.NotFound);
        response.StatusMessage.ShouldBe(GetTranscriptQuery.NotFoundMessage);
        await _catalog.DidNotReceive().GetTranscriptAsync(Arg.Any<int>(), Arg.Any<AccessContext>(), Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "ТБ-020/021: вне допуска (каталог вернул null) → тот же NotFound, неотличимо от несуществующего")]
    public async Task Get_transcript_outside_clearance_is_not_found()
    {
        _catalog.GetTranscriptAsync(50, Arg.Any<AccessContext>(), Arg.Any<CancellationToken>()).Returns((MediaTranscript?)null);

        var response = await GetAsync(new GetTranscriptQuery(50));

        response.StatusCode.ShouldBe(ResponseStatusCode.NotFound);
        response.StatusMessage.ShouldBe(GetTranscriptQuery.NotFoundMessage);
    }

    [Fact(DisplayName = "ТБ-021: без контекста доступа — отказ исключением, каталог не вызывается")]
    public async Task Get_transcript_without_access_context_fails_closed()
    {
        _accessProvider.GetCurrentAsync(Arg.Any<CancellationToken>())
            .Returns<AccessContext>(_ => throw new AccessContextRequiredException());

        await Should.ThrowAsync<AccessContextRequiredException>(() => GetAsync(new GetTranscriptQuery(50)).AsTask());
        await _catalog.DidNotReceive().GetTranscriptAsync(Arg.Any<int>(), Arg.Any<AccessContext>(), Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "Чтение расшифровки — НЕ отдельная запись журнала: грузится с карточкой, чьё открытие уже аудируется")]
    public void Get_transcript_is_not_separately_audited()
    {
        typeof(IAuditableRequest).IsAssignableFrom(typeof(GetTranscriptQuery)).ShouldBeFalse();
        typeof(IAuditableRequest).IsAssignableFrom(typeof(GetMediaAssetQuery)).ShouldBeTrue();
    }

    // ---------- SearchTranscriptsQuery ----------

    [Fact(DisplayName = "Поиск: область — носители доступного дела, текст обрезан, предел выдачи передан каталогу")]
    public async Task Search_uses_case_assets_trimmed_text_and_limit()
    {
        IReadOnlyList<TranscriptHit> hits = [new TranscriptHit(50, "voice.ogg", MediaKind.Audio, 0, 0, 1500, "салам")];
        _catalog.SearchTranscriptsAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<AccessContext>(), Arg.Any<CancellationToken>())
            .Returns(hits);

        var response = await SearchAsync(new SearchTranscriptsQuery(3, "  салам "));

        response.Status.ShouldBeTrue();
        response.Data!.ShouldHaveSingleItem().AssetId.ShouldBe(50);
        await _catalog.Received(1).SearchTranscriptsAsync(
            Arg.Is<IReadOnlyCollection<int>>(ids => ids.Count == 2 && ids.Contains(50) && ids.Contains(51)),
            "салам",
            SearchTranscriptsQuery.MaxHits,
            Access,
            Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "ТБ-071: недоступное дело → NotFound, носители дела не запрашиваются, каталог не вызывается")]
    public async Task Search_in_inaccessible_case_is_not_found()
    {
        var response = await SearchAsync(new SearchTranscriptsQuery(99, "салам"));

        response.StatusCode.ShouldBe(ResponseStatusCode.NotFound);
        await _caseScope.DidNotReceive().GetAssetIdsAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>());
        await _catalog.DidNotReceive().SearchTranscriptsAsync(
            Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<AccessContext>(), Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "Дело без носителей: пустая выдача без обращения к каталогу")]
    public async Task Search_in_case_without_assets_is_empty()
    {
        _caseScope.GetAssetIdsAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>()).Returns([]);

        var response = await SearchAsync(new SearchTranscriptsQuery(3, "салам"));

        response.Status.ShouldBeTrue();
        response.Data!.ShouldBeEmpty();
        await _catalog.DidNotReceive().SearchTranscriptsAsync(
            Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<AccessContext>(), Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "ТБ-030: поиск аудируется как Search, в записи — дело и искомый текст (обрезанный, не длиннее предела)")]
    public void Search_is_audited_with_case_and_text()
    {
        var query = new SearchTranscriptsQuery(3, "  үйгө бардым ");
        query.ShouldBeAssignableTo<IAuditableRequest>();
        query.AuditAction.ShouldBe(AuditAction.Search);
        query.AuditSummary.ShouldBe("media:transcripts:search:case=3;text=үйгө бардым");

        var longQuery = new SearchTranscriptsQuery(3, new string('ы', 5000));
        longQuery.AuditSummary!.Length.ShouldBeLessThan(SearchTranscriptsQuery.MaxTextLength + 64);
    }

    [Theory(DisplayName = "Валидатор поиска: дело > 0, текст 2..200 символов после обрезки пробелов")]
    [InlineData(3, "үй", true)]
    [InlineData(3, "  үй  ", true)]
    [InlineData(3, "ү", false)]
    [InlineData(3, "   ы   ", false)]
    [InlineData(3, "", false)]
    [InlineData(3, null, false)]
    [InlineData(0, "салам", false)]
    [InlineData(-1, "салам", false)]
    public void Search_validator_rules(int caseId, string? text, bool expected)
    {
        new SearchTranscriptsValidator().Validate(new SearchTranscriptsQuery(caseId, text!)).IsValid.ShouldBe(expected);
    }

    [Fact(DisplayName = "Валидатор поиска: ровно 200 символов — можно, 201 — нельзя (пробелы по краям не считаются)")]
    public void Search_validator_length_boundary()
    {
        var validator = new SearchTranscriptsValidator();
        validator.Validate(new SearchTranscriptsQuery(3, " " + new string('а', SearchTranscriptsQuery.MaxTextLength) + " ")).IsValid.ShouldBeTrue();
        validator.Validate(new SearchTranscriptsQuery(3, new string('а', SearchTranscriptsQuery.MaxTextLength + 1))).IsValid.ShouldBeFalse();
    }

    // ---------- RetranscribeMediaCommand ----------

    [Fact(DisplayName = "Повторная расшифровка аудио: статус «в очереди», задача поставлена, возвращён её идентификатор; запуск аудируется")]
    public async Task Retranscribe_marks_pending_and_enqueues()
    {
        var response = await RetranscribeAsync(new RetranscribeMediaCommand(50));

        response.Status.ShouldBeTrue();
        response.Data.ShouldBe(_queue.LastId);
        await _store.Received(1).TryMarkTranscriptionPendingAsync(50, Arg.Any<CancellationToken>());
        _queue.Kinds.ShouldBe([RetranscribeMediaCommand.TaskKind]);

        var command = new RetranscribeMediaCommand(50);
        command.AuditAction.ShouldBe(AuditAction.Modify);
        command.AuditSummary.ShouldBe("media:retranscribe:asset=50");
    }

    [Fact(DisplayName = "Расшифровка уже в очереди или идёт (условная постановка в БД не прошла) → BadRequest «уже выполняется — нажмите «Обновить»», второй прогон не ставится")]
    public async Task Retranscribe_while_in_progress_is_bad_request()
    {
        _store.TryMarkTranscriptionPendingAsync(50, Arg.Any<CancellationToken>()).Returns(false);

        var response = await RetranscribeAsync(new RetranscribeMediaCommand(50));

        response.StatusCode.ShouldBe(ResponseStatusCode.BadRequest);
        response.StatusMessage.ShouldBe(RetranscribeMediaCommand.AlreadyInProgressMessage);
        response.StatusMessage.ShouldContain("Обновить");
        await _store.Received(1).TryMarkTranscriptionPendingAsync(50, Arg.Any<CancellationToken>());
        _queue.Kinds.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Видео (в т.ч. загруженное до ADR-0026) можно отправить на расшифровку")]
    public async Task Retranscribe_video_is_allowed()
    {
        _catalog.GetAsync(50, Arg.Any<AccessContext>(), Arg.Any<CancellationToken>()).Returns(Asset(50, MediaKind.Video));

        (await RetranscribeAsync(new RetranscribeMediaCommand(50))).Status.ShouldBeTrue();
        _queue.Kinds.Count.ShouldBe(1);
    }

    [Fact(DisplayName = "Без права загрузки → BadRequest до чтения носителя, ничего не ставится")]
    public async Task Retranscribe_without_right_is_bad_request()
    {
        _administration.CanUploadAsync(Arg.Any<CancellationToken>()).Returns(false);

        var response = await RetranscribeAsync(new RetranscribeMediaCommand(50));

        response.StatusCode.ShouldBe(ResponseStatusCode.BadRequest);
        await _catalog.DidNotReceive().GetAsync(Arg.Any<int>(), Arg.Any<AccessContext>(), Arg.Any<CancellationToken>());
        await _store.DidNotReceive().TryMarkTranscriptionPendingAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
        _queue.Kinds.ShouldBeEmpty();
    }

    [Fact(DisplayName = "ТБ-020/071: носитель вне допуска или чужого дела → NotFound (неразличимо), ничего не ставится")]
    public async Task Retranscribe_inaccessible_asset_is_not_found()
    {
        // Вне допуска: каталог (решётка на стороне БД) носитель не отдаёт.
        var outside = await RetranscribeAsync(new RetranscribeMediaCommand(77));
        outside.StatusCode.ShouldBe(ResponseStatusCode.NotFound);

        // В допуске, но носитель чужого дела.
        _caseScope.IsAssetAccessibleAsync(50, Arg.Any<AccessContext>(), Arg.Any<CancellationToken>()).Returns(false);
        var foreign = await RetranscribeAsync(new RetranscribeMediaCommand(50));
        foreign.StatusCode.ShouldBe(ResponseStatusCode.NotFound);
        foreign.StatusMessage.ShouldBe(outside.StatusMessage);

        await _store.DidNotReceive().TryMarkTranscriptionPendingAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
        _queue.Kinds.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Изображение → BadRequest «к изображению расшифровка неприменима», статус не меняется, задача не ставится")]
    public async Task Retranscribe_image_is_not_applicable()
    {
        _catalog.GetAsync(50, Arg.Any<AccessContext>(), Arg.Any<CancellationToken>()).Returns(Asset(50, MediaKind.Image));

        var response = await RetranscribeAsync(new RetranscribeMediaCommand(50));

        response.StatusCode.ShouldBe(ResponseStatusCode.BadRequest);
        response.StatusMessage.ShouldContain("неприменима");
        await _store.DidNotReceive().TryMarkTranscriptionPendingAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
        _queue.Kinds.ShouldBeEmpty();
    }

    // ---------- ReindexMediaCommand для аудио ----------

    [Fact(DisplayName = "ADR-0026: переиндексация лиц аудиозаписи → BadRequest, задача не ставится")]
    public async Task Reindex_audio_is_bad_request()
    {
        _caseScope.IsBiometricIndexingAllowedAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(true);
        var handler = new ReindexMediaCommand.Handler(_administration, _accessProvider, _catalog, _caseScope, _queue);

        var response = await handler.Handle(new ReindexMediaCommand(50), CancellationToken.None);

        response.StatusCode.ShouldBe(ResponseStatusCode.BadRequest);
        response.StatusMessage.ShouldContain("аудиозаписи");
        _queue.Kinds.ShouldBeEmpty();
    }

    private static MediaAssetRow Asset(int id, MediaKind kind) =>
        new(id, kind, "file", "a1b2.bin", "audio/ogg", 10, 1000, null, null, 2, 1, 7,
            kind == MediaKind.Audio ? MediaIndexStatus.NotApplicable : MediaIndexStatus.Indexed,
            null, null, null, null, DateTime.UtcNow, 0,
            kind == MediaKind.Image ? TranscriptStatus.NotApplicable : TranscriptStatus.Done);

    private ValueTask<ResponseDto<MediaTranscript>> GetAsync(GetTranscriptQuery query) =>
        new GetTranscriptQuery.Handler(_accessProvider, _catalog, _caseScope).Handle(query, CancellationToken.None);

    private ValueTask<ResponseDto<IReadOnlyList<TranscriptHit>>> SearchAsync(SearchTranscriptsQuery query) =>
        new SearchTranscriptsQuery.Handler(_accessProvider, _caseScope, _catalog).Handle(query, CancellationToken.None);

    private ValueTask<ResponseDto<Guid>> RetranscribeAsync(RetranscribeMediaCommand command) =>
        new RetranscribeMediaCommand.Handler(_administration, _accessProvider, _catalog, _caseScope, _store, _queue)
            .Handle(command, CancellationToken.None);

    /// <summary>Очередь-регистратор: запоминает виды поставленных задач, не исполняя их.</summary>
    private sealed class RecordingQueue : IBackgroundTaskQueue
    {
        public List<string> Kinds { get; } = [];

        public Guid LastId { get; private set; }

        public ValueTask<Guid> EnqueueAsync(
            string kind, Func<IServiceProvider, CancellationToken, Task> work, CancellationToken cancellationToken = default)
        {
            Kinds.Add(kind);
            LastId = Guid.NewGuid();
            return ValueTask.FromResult(LastId);
        }
    }
}
