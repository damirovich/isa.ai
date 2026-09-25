using Xunit;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Bunit;
using ISC.AI.Abstractions.Application;
using ISC.AI.Modules.Media.Application.Features.Assets;
using ISC.AI.Modules.Media.Application.Features.Scope;
using ISC.AI.Modules.Media.Application.Features.Transcripts;
using ISC.AI.Modules.Media.Application.Features.Verification;
using ISC.AI.Modules.Media.Domain.Model;
using ISC.AI.Modules.Media.Domain.Services;
using ISC.AI.Modules.Media.UI;
using Mediator;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using NSubstitute;
using Shouldly;

namespace ISC.AI.UnitTests.Media.UI;

public sealed class MediaUiComponentTests : BunitContext, IAsyncLifetime
{
    private const string ModulePath = "./_content/ISC.AI.Modules.Media.UI/js/media.js";
    private readonly IMediator _mediator = Substitute.For<IMediator>();
    private readonly BunitJSModuleInterop _module;

    public MediaUiComponentTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        _module = JSInterop.SetupModule(ModulePath);
        _module.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices();
        Services.AddSingleton(_mediator);
        SetRendererInfo(new RendererInfo("Server", true));
    }

    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;

    Task IAsyncLifetime.DisposeAsync() => base.DisposeAsync().AsTask();

    private static MediaTranscript Transcript(TranscriptStatus status, string? error = null, params TranscriptSegmentRow[] segments) =>
        new(5, status, error, "gigaam-v3-ctc-multilang@1", new DateTime(2026, 9, 25, 8, 0, 0, DateTimeKind.Utc), segments);

    private void SetupTranscript(MediaTranscript transcript) =>
        _mediator.Send(Arg.Any<GetTranscriptQuery>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<ResponseDto<MediaTranscript>>(ResponseDto<MediaTranscript>.Ok(transcript)));

    [Fact(DisplayName = "Расшифровка готова: фрагменты с таймкодами, текст экранирован, есть пометка «требует проверки» и модель")]
    public void Panel_done_renders_segments_escaped_with_note_and_model()
    {
        SetupTranscript(Transcript(TranscriptStatus.Done, null,
            new TranscriptSegmentRow(0, 1_000, 4_000, "салам <img src=x onerror=alert(1)>"),
            new TranscriptSegmentRow(1, 65_000, 70_000, "экинчи бөлүк")));

        var cut = Render<TranscriptPanel>(p => p
            .Add(x => x.AssetId, 5)
            .Add(x => x.PlayerElementId, "media-audio-5"));

        cut.Markup.ShouldContain("Автоматическая расшифровка: дословный текст модели распознавания, без знаков препинания. Требует проверки и не является протоколом.");
        cut.Markup.ShouldContain("gigaam-v3-ctc-multilang@1");
        cut.Markup.ShouldContain("[00:01]");
        cut.Markup.ShouldContain("[01:05]");
        cut.Markup.ShouldContain("&lt;img src=x onerror=alert(1)&gt;");
        cut.Markup.ShouldNotContain("<img");
        cut.Markup.ShouldContain("Расшифровать заново");
        cut.Markup.ShouldNotContain(">Обновить<");
    }

    [Fact(DisplayName = "Щелчок по фрагменту перематывает проигрыватель к его началу и запускает воспроизведение")]
    public void Panel_click_segment_seeks_and_plays()
    {
        SetupTranscript(Transcript(TranscriptStatus.Done, null,
            new TranscriptSegmentRow(0, 1_000, 4_000, "бир"),
            new TranscriptSegmentRow(1, 65_000, 70_000, "эки")));

        var cut = Render<TranscriptPanel>(p => p
            .Add(x => x.AssetId, 5)
            .Add(x => x.PlayerElementId, "media-audio-5"));

        cut.Find("#media-transcript-5-1").Click();
        var seek = _module.Invocations["seek"].ShouldHaveSingleItem();
        seek.Arguments[0].ShouldBe("media-audio-5");
        seek.Arguments[1].ShouldBe(65.0);
        seek.Arguments[2].ShouldBe(true);
        cut.Find("#media-transcript-5-1").GetAttribute("style")!.ShouldContain("var(--mud-palette-primary)");
    }

    [Fact(DisplayName = "Фрагмент — нативная кнопка (Enter и пробел работают сами), без role/tabindex и без <p> внутри")]
    public void Panel_segment_is_native_button()
    {
        SetupTranscript(Transcript(TranscriptStatus.Done, null, new TranscriptSegmentRow(0, 1_000, 4_000, "бир")));

        var cut = Render<TranscriptPanel>(p => p
            .Add(x => x.AssetId, 5)
            .Add(x => x.PlayerElementId, "media-audio-5"));

        var row = cut.Find("#media-transcript-5-0");
        row.TagName.ShouldBe("BUTTON");
        row.GetAttribute("type").ShouldBe("button");
        row.GetAttribute("title").ShouldBe("Воспроизвести с этого места");
        row.HasAttribute("role").ShouldBeFalse();
        row.HasAttribute("tabindex").ShouldBeFalse();
        row.QuerySelectorAll("p").ShouldBeEmpty();
        row.TextContent.ShouldContain("[00:01]");
        row.TextContent.ShouldContain("бир");
    }

    [Fact(DisplayName = "Смена активного фрагмента перерисовывает только прежнюю и новую строки, а не весь список")]
    public void Panel_click_rerenders_only_changed_rows()
    {
        SetupTranscript(Transcript(TranscriptStatus.Done, null,
            new TranscriptSegmentRow(0, 1_000, 4_000, "бир"),
            new TranscriptSegmentRow(1, 65_000, 70_000, "эки"),
            new TranscriptSegmentRow(2, 90_000, 95_000, "үч")));

        var cut = Render<TranscriptPanel>(p => p
            .Add(x => x.AssetId, 5)
            .Add(x => x.PlayerElementId, "media-audio-5"));
        var rows = cut.FindComponents<TranscriptSegmentItem>();
        rows.Count.ShouldBe(3);
        var before = rows.Select(r => r.RenderCount).ToArray();

        cut.Find("#media-transcript-5-1").Click();
        rows.Select((r, i) => r.RenderCount - before[i]).ShouldBe([0, 1, 0]);

        cut.Find("#media-transcript-5-2").Click();
        rows.Select((r, i) => r.RenderCount - before[i]).ShouldBe([0, 2, 1]);
        cut.Find("#media-transcript-5-2").GetAttribute("style")!.ShouldContain("var(--mud-palette-primary)");
        cut.Find("#media-transcript-5-1").GetAttribute("style")!.ShouldNotContain("var(--mud-palette-primary)");

        var seeks = _module.Invocations["seek"];
        seeks.Count.ShouldBe(2);
        seeks[1].Arguments[1].ShouldBe(90.0);
    }

    [Fact(DisplayName = "Переход из поиска (?t=): фрагмент подсвечен, проигрыватель перемотан без запуска звука")]
    public void Panel_start_at_highlights_and_seeks_without_play()
    {
        SetupTranscript(Transcript(TranscriptStatus.Done, null,
            new TranscriptSegmentRow(0, 1_000, 4_000, "бир"),
            new TranscriptSegmentRow(1, 65_000, 70_000, "эки")));

        var cut = Render<TranscriptPanel>(p => p
            .Add(x => x.AssetId, 5)
            .Add(x => x.PlayerElementId, "media-video-5")
            .Add(x => x.StartAtMs, 65_000L));

        cut.WaitForAssertion(() => _module.Invocations["reveal"].ShouldHaveSingleItem().Arguments[0].ShouldBe("media-transcript-5-1"));
        var seek = _module.Invocations["seek"].ShouldHaveSingleItem();
        seek.Arguments[0].ShouldBe("media-video-5");
        seek.Arguments[1].ShouldBe(65.0);
        seek.Arguments[2].ShouldBe(false);
        cut.Find("#media-transcript-5-1").GetAttribute("style")!.ShouldContain("var(--mud-palette-primary)");
        cut.Find("#media-transcript-5-0").GetAttribute("style")!.ShouldNotContain("var(--mud-palette-primary)");
    }

    [Fact(DisplayName = "Ноль фрагментов — «речь не обнаружена», а не пустая панель")]
    public void Panel_done_zero_segments_says_no_speech()
    {
        SetupTranscript(Transcript(TranscriptStatus.Done));
        var cut = Render<TranscriptPanel>(p => p.Add(x => x.AssetId, 5));
        cut.Markup.ShouldContain("Речь не обнаружена");
    }

    [Fact(DisplayName = "В обработке — «Обновить»; при ошибке — причина и «Расшифровать заново», которая отправляет команду")]
    public void Panel_processing_has_refresh_and_failed_shows_reason_and_retranscribe_sends_command()
    {
        SetupTranscript(Transcript(TranscriptStatus.Processing));
        var cut = Render<TranscriptPanel>(p => p.Add(x => x.AssetId, 5));
        cut.Markup.ShouldContain("Идёт расшифровка");
        cut.FindAll("button").Any(b => b.TextContent.Contains("Обновить")).ShouldBeTrue();
        // Во время расшифровки повторный запуск неактивен — нажимать надо «Обновить».
        cut.FindAll("button").First(b => b.TextContent.Contains("Расшифровать заново")).HasAttribute("disabled").ShouldBeTrue();

        SetupTranscript(Transcript(TranscriptStatus.Failed, "ffmpeg: нет звуковой дорожки"));
        cut.FindAll("button").First(b => b.TextContent.Contains("Обновить")).Click();
        cut.Markup.ShouldContain("ffmpeg: нет звуковой дорожки");

        _mediator.Send(Arg.Any<RetranscribeMediaCommand>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<ResponseDto<Guid>>(ResponseDto<Guid>.Ok(Guid.NewGuid())));
        SetupTranscript(Transcript(TranscriptStatus.Pending));
        var retranscribe = cut.FindAll("button").First(b => b.TextContent.Contains("Расшифровать заново"));
        retranscribe.HasAttribute("disabled").ShouldBeFalse();
        retranscribe.Click();
        _mediator.Received(1).Send(Arg.Is<RetranscribeMediaCommand>(c => c.AssetId == 5), Arg.Any<CancellationToken>());
        cut.Markup.ShouldContain("поставлен в очередь на расшифровку");
        cut.FindAll("button").First(b => b.TextContent.Contains("Расшифровать заново")).HasAttribute("disabled").ShouldBeTrue();
    }

    [Fact(DisplayName = "В очереди или в работе «Расшифровать заново» неактивна")]
    public void Panel_pending_retranscribe_disabled()
    {
        SetupTranscript(Transcript(TranscriptStatus.Pending, null, new TranscriptSegmentRow(0, 1_000, 4_000, "бир")));
        var cut = Render<TranscriptPanel>(p => p.Add(x => x.AssetId, 5));
        cut.Markup.ShouldContain("(в очереди)");
        cut.FindAll("button").First(b => b.TextContent.Contains("Расшифровать заново")).HasAttribute("disabled").ShouldBeTrue();
        cut.FindAll("button").First(b => b.TextContent.Contains("Обновить")).HasAttribute("disabled").ShouldBeFalse();
    }

    [Fact(DisplayName = "Отказ повторной расшифровки показан как ошибка, состояние перечитано — видна «Обновить»")]
    public void Panel_retranscribe_refusal_shows_error_and_reloads_status()
    {
        SetupTranscript(Transcript(TranscriptStatus.Done, null, new TranscriptSegmentRow(0, 1_000, 4_000, "бир")));
        var cut = Render<TranscriptPanel>(p => p.Add(x => x.AssetId, 5));
        cut.FindAll("button").Any(b => b.TextContent.Contains("Обновить")).ShouldBeFalse();

        // Расшифровку тем временем запустил другой сотрудник — сервер отклоняет повторный запуск.
        _mediator.Send(Arg.Any<RetranscribeMediaCommand>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<ResponseDto<Guid>>(
                ResponseDto<Guid>.BadRequest("Расшифровка уже выполняется — нажмите «Обновить»")));
        SetupTranscript(Transcript(TranscriptStatus.Processing, null, new TranscriptSegmentRow(0, 1_000, 4_000, "бир")));
        cut.FindAll("button").First(b => b.TextContent.Contains("Расшифровать заново")).Click();

        cut.FindAll(".mud-alert").Any(a => a.TextContent.Contains("Расшифровка уже выполняется — нажмите «Обновить»")).ShouldBeTrue();
        cut.Markup.ShouldNotContain("поставлен в очередь на расшифровку");
        cut.FindAll("button").Any(b => b.TextContent.Contains("Обновить")).ShouldBeTrue();
        cut.FindAll("button").First(b => b.TextContent.Contains("Расшифровать заново")).HasAttribute("disabled").ShouldBeTrue();
    }

    [Fact(DisplayName = "Формат, который браузер не играет: фрагменты не кликабельны")]
    public void Panel_not_playable_segments_not_clickable()
    {
        SetupTranscript(Transcript(TranscriptStatus.Done, null, new TranscriptSegmentRow(0, 1_000, 4_000, "бир")));
        var cut = Render<TranscriptPanel>(p => p.Add(x => x.AssetId, 5));
        cut.Markup.ShouldContain("переход к месту записи недоступен");
        var row = cut.Find("#media-transcript-5-0");
        row.TagName.ShouldBe("DIV");
        row.HasAttribute("title").ShouldBeFalse();
        row.TextContent.ShouldContain("бир");
    }

    [Fact(DisplayName = "На предрендере расшифровка не запрашивается — без двойной записи в журнал")]
    public void Panel_prerender_does_not_load()
    {
        SetRendererInfo(new RendererInfo("Static", false));
        SetupTranscript(Transcript(TranscriptStatus.Done));
        var cut = Render<TranscriptPanel>(p => p.Add(x => x.AssetId, 5));
        _mediator.DidNotReceive().Send(Arg.Any<GetTranscriptQuery>(), Arg.Any<CancellationToken>());
        cut.Markup.ShouldContain("mud-progress-linear");
    }

    private static MediaAssetRow Asset(MediaKind kind, string contentType) => new(
        5, kind, "voice.ogg", "0123456789abcdef0123456789abcdef.ogg", contentType, 1024, 70_000, null, null,
        0, 1, 1, kind == MediaKind.Audio ? MediaIndexStatus.NotApplicable : MediaIndexStatus.Indexed,
        null, null, null, null, DateTime.UtcNow, 0, TranscriptStatus.Done);

    [Fact(DisplayName = "Карточка аудио: проигрыватель, без блоков лиц и переиндексации, ?t= перематывает")]
    public void AssetCard_audio_player_no_faces_and_query_t()
    {
        Render<MudPopoverProvider>();
        _mediator.Send(Arg.Any<GetMediaAssetQuery>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<ResponseDto<MediaAssetDetails>>(ResponseDto<MediaAssetDetails>.Ok(
                new MediaAssetDetails(Asset(MediaKind.Audio, "audio/ogg"), [], 3))));
        SetupTranscript(Transcript(TranscriptStatus.Done, null,
            new TranscriptSegmentRow(0, 1_000, 4_000, "бир"),
            new TranscriptSegmentRow(1, 65_000, 70_000, "эки")));

        Services.GetRequiredService<NavigationManager>().NavigateTo("/media/assets/5?t=65000");
        var cut = Render<ISC.AI.Modules.Media.UI.AssetCard>(p => p.Add(x => x.Id, 5));

        cut.WaitForAssertion(() => _module.Invocations["reveal"].ShouldNotBeEmpty());
        var audio = cut.Find("audio#media-audio-5");
        audio.GetAttribute("src").ShouldBe("/media/files/media-originals/5/0123456789abcdef0123456789abcdef.ogg");
        audio.GetAttribute("preload").ShouldBe("metadata");
        cut.Markup.ShouldNotContain("Лица на носителе");
        cut.Markup.ShouldNotContain("Переиндексировать");
        cut.Markup.ShouldContain("Расшифровка речи");
        _module.Invocations["seek"].Last().Arguments[0].ShouldBe("media-audio-5");
        _module.Invocations["seek"].Last().Arguments[1].ShouldBe(65.0);
    }

    [Fact(DisplayName = "Карточка AMR: вместо проигрывателя — ссылка «Скачать оригинал»")]
    public void AssetCard_amr_download_link()
    {
        Render<MudPopoverProvider>();
        _mediator.Send(Arg.Any<GetMediaAssetQuery>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<ResponseDto<MediaAssetDetails>>(ResponseDto<MediaAssetDetails>.Ok(
                new MediaAssetDetails(Asset(MediaKind.Audio, "audio/amr"), [], 3))));
        SetupTranscript(Transcript(TranscriptStatus.Done, null, new TranscriptSegmentRow(0, 1_000, 4_000, "бир")));
        var cut = Render<ISC.AI.Modules.Media.UI.AssetCard>(p => p.Add(x => x.Id, 5));
        cut.FindAll("audio").ShouldBeEmpty();
        cut.Markup.ShouldContain("Скачать оригинал");
        cut.Markup.ShouldContain("не воспроизводит формат AMR");
        cut.Markup.ShouldContain("переход к месту записи недоступен");
    }

    [Theory(DisplayName = "Аудиозапись в видеоконтейнере (индексатор перевёл «видео» без картинки в аудио): mp4/webm играет <audio>, 3GP — «скачать»")]
    [InlineData("video/mp4", true)]
    [InlineData("video/webm", true)]
    [InlineData("video/3gpp", false)]
    public void AssetCard_audio_in_video_container(string contentType, bool playable)
    {
        Render<MudPopoverProvider>();
        _mediator.Send(Arg.Any<GetMediaAssetQuery>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<ResponseDto<MediaAssetDetails>>(ResponseDto<MediaAssetDetails>.Ok(
                new MediaAssetDetails(Asset(MediaKind.Audio, contentType), [], 3))));
        SetupTranscript(Transcript(TranscriptStatus.Done, null, new TranscriptSegmentRow(0, 1_000, 4_000, "бир")));
        var cut = Render<ISC.AI.Modules.Media.UI.AssetCard>(p => p.Add(x => x.Id, 5));

        cut.FindAll("video").ShouldBeEmpty("вид носителя — аудио: видеопроигрывателя нет");
        if (playable)
        {
            cut.FindAll("audio").Count.ShouldBe(1);
            cut.Markup.ShouldNotContain("Скачать оригинал");
        }
        else
        {
            cut.FindAll("audio").ShouldBeEmpty();
            cut.Markup.ShouldContain("не воспроизводит формат 3GP");
        }
    }

    [Fact(DisplayName = "Карточка видео: лица на месте и есть панель расшифровки")]
    public void AssetCard_video_has_faces_and_transcript()
    {
        Render<MudPopoverProvider>();
        _mediator.Send(Arg.Any<GetMediaAssetQuery>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<ResponseDto<MediaAssetDetails>>(ResponseDto<MediaAssetDetails>.Ok(
                new MediaAssetDetails(Asset(MediaKind.Video, "video/mp4"), [], 3))));
        SetupTranscript(Transcript(TranscriptStatus.NotApplicable));
        var cut = Render<ISC.AI.Modules.Media.UI.AssetCard>(p => p.Add(x => x.Id, 5));
        cut.Find("video#media-video-5");
        cut.Markup.ShouldContain("Лица на носителе");
        cut.Markup.ShouldContain("Переиндексировать");
        cut.Markup.ShouldContain("не выполнялась");
        cut.FindAll("button").Any(b => b.TextContent.Trim() == "Расшифровать").ShouldBeTrue();
    }

    [Theory(DisplayName = "Нечисловые или переполненные ?t= и ?face= игнорируются — карточка открывается с начала записи")]
    [InlineData("/media/assets/5?t=abc&face=xyz")]
    [InlineData("/media/assets/5?t=99999999999999999999&face=99999999999")]
    [InlineData("/media/assets/5?t=-5&face=-1")]
    [InlineData("/media/assets/5?t=1e5&face=1.5")]
    public void AssetCard_bad_query_values_ignored(string url)
    {
        Render<MudPopoverProvider>();
        _mediator.Send(Arg.Any<GetMediaAssetQuery>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<ResponseDto<MediaAssetDetails>>(ResponseDto<MediaAssetDetails>.Ok(
                new MediaAssetDetails(Asset(MediaKind.Audio, "audio/ogg"), [], 3))));
        SetupTranscript(Transcript(TranscriptStatus.Done, null, new TranscriptSegmentRow(0, 1_000, 4_000, "бир")));

        Services.GetRequiredService<NavigationManager>().NavigateTo(url);
        var cut = Render<ISC.AI.Modules.Media.UI.AssetCard>(p => p.Add(x => x.Id, 5));

        cut.Find("audio#media-audio-5");
        cut.Find("#media-transcript-5-0");
        _module.Invocations["seek"].ShouldBeEmpty();
        _module.Invocations["reveal"].ShouldBeEmpty();
        cut.Find("#media-transcript-5-0").GetAttribute("style")!.ShouldNotContain("var(--mud-palette-primary)");
    }

    [Theory(DisplayName = "Поиск по лицу: ?caseId= и ?faceId= — корректные подставляются, нечисловые игнорируются без падения")]
    [InlineData("/media/search?caseId=3&faceId=11", true)]
    [InlineData("/media/search?caseId=abc&faceId=xyz", false)]
    [InlineData("/media/search?caseId=99999999999&faceId=-1", false)]
    [InlineData("/media/search?caseId=3.0&faceId=1e1", false)]
    public void FaceSearch_query_values_parsed_or_ignored(string url, bool valid)
    {
        Render<MudPopoverProvider>();
        _mediator.Send(Arg.Any<ListAccessibleCasesQuery>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<ResponseDto<IReadOnlyList<CaseScopeItem>>>(
                ResponseDto<IReadOnlyList<CaseScopeItem>>.Ok([new CaseScopeItem(3, "12-345", "Дело", 0, 1)])));
        _mediator.Send(Arg.Any<ListCaseAuthorizationsQuery>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<ResponseDto<IReadOnlyList<CaseAuthorizationItem>>>(
                ResponseDto<IReadOnlyList<CaseAuthorizationItem>>.Ok([new CaseAuthorizationItem(9, "Постановление № 1")])));
        _mediator.Send(Arg.Any<GetFaceQuery>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<ResponseDto<FaceRow>>(ResponseDto<FaceRow>.NotFound()));

        Services.GetRequiredService<NavigationManager>().NavigateTo(url);
        var cut = Render<ISC.AI.Modules.Media.UI.FaceSearch>();

        cut.Markup.ShouldContain("Поиск по лицу");
        if (valid)
        {
            _mediator.Received(1).Send(Arg.Is<ListCaseAuthorizationsQuery>(q => q.CaseId == 3), Arg.Any<CancellationToken>());
            _mediator.Received(1).Send(Arg.Is<GetFaceQuery>(q => q.FaceId == 11), Arg.Any<CancellationToken>());
        }
        else
        {
            _mediator.DidNotReceive().Send(Arg.Any<ListCaseAuthorizationsQuery>(), Arg.Any<CancellationToken>());
            _mediator.DidNotReceive().Send(Arg.Any<GetFaceQuery>(), Arg.Any<CancellationToken>());
        }
    }

    [Theory(DisplayName = "Решение по кандидату: ?person= — корректный фигурант подставляется, нечисловой игнорируется без падения")]
    [InlineData("/media/verification/21?stage=Expert&person=7", true)]
    [InlineData("/media/verification/21?stage=Expert&person=abc", false)]
    [InlineData("/media/verification/21?stage=Expert&person=99999999999", false)]
    [InlineData("/media/verification/21?stage=Expert&person=-7", false)]
    public void CandidateVerification_person_query_parsed_or_ignored(string url, bool valid)
    {
        Render<MudPopoverProvider>();
        _mediator.Send(Arg.Any<GetCandidatePairQuery>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<ResponseDto<VerificationQueueItem>>(ResponseDto<VerificationQueueItem>.Ok(
                new VerificationQueueItem(21, 4, 3, 1, 11, 5, null, null, 0.61, null, "probe.jpg", new string('a', 64), null,
                    0, 1, CandidateStatus.Candidate, null))));
        _mediator.Send(Arg.Any<ListCasePersonsQuery>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<ResponseDto<IReadOnlyList<CasePersonItem>>>(
                ResponseDto<IReadOnlyList<CasePersonItem>>.Ok([new CasePersonItem(7, "Фигурант")])));

        Services.GetRequiredService<NavigationManager>().NavigateTo(url);
        var cut = Render<ISC.AI.Modules.Media.UI.CandidateVerification>(p => p.Add(x => x.CandidateId, 21));

        cut.Markup.ShouldContain("Решение стадии");
        if (valid)
        {
            cut.Markup.ShouldContain("Кандидат создан ручной привязкой к фигуранту № 7");
            cut.Markup.ShouldContain("он подставлен в список");
        }
        else
        {
            cut.Markup.ShouldNotContain("Кандидат создан ручной привязкой");
        }
    }

    [Fact(DisplayName = "Карточка видео 3GP: вместо проигрывателя — «скачать», в подсказке назван именно 3GP")]
    public void AssetCard_3gp_video_names_its_container()
    {
        Render<MudPopoverProvider>();
        _mediator.Send(Arg.Any<GetMediaAssetQuery>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<ResponseDto<MediaAssetDetails>>(ResponseDto<MediaAssetDetails>.Ok(
                new MediaAssetDetails(Asset(MediaKind.Video, "video/3gpp"), [], 3))));
        SetupTranscript(Transcript(TranscriptStatus.Done, null, new TranscriptSegmentRow(0, 1_000, 4_000, "бир")));

        var cut = Render<ISC.AI.Modules.Media.UI.AssetCard>(p => p.Add(x => x.Id, 5));

        cut.FindAll("video").ShouldBeEmpty();
        cut.Markup.ShouldContain("Скачать оригинал");
        cut.Markup.ShouldContain("Браузер не воспроизводит контейнер 3GP");
        cut.Markup.ShouldNotContain("MKV/AVI/MOV");
        cut.Markup.ShouldContain("переход к месту записи недоступен");
    }

    [Fact(DisplayName = "Поиск по расшифровкам дела: найденное выделено, разметка экранирована, ссылка ведёт к месту записи")]
    public void CaseMedia_search_highlights_escaped_and_links_with_t()
    {
        Render<MudPopoverProvider>();
        _mediator.Send(Arg.Any<ListAccessibleCasesQuery>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<ResponseDto<IReadOnlyList<CaseScopeItem>>>(
                ResponseDto<IReadOnlyList<CaseScopeItem>>.Ok([new CaseScopeItem(3, "12-345", "Дело", 0, 1)])));
        _mediator.Send(Arg.Any<ListCaseMediaQuery>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<ResponseDto<IReadOnlyList<MediaAssetRow>>>(
                ResponseDto<IReadOnlyList<MediaAssetRow>>.Ok([Asset(MediaKind.Audio, "audio/ogg")])));
        _mediator.Send(Arg.Any<SearchTranscriptsQuery>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<ResponseDto<IReadOnlyList<TranscriptHit>>>(
                ResponseDto<IReadOnlyList<TranscriptHit>>.Ok([
                    new TranscriptHit(7, "voice<1>.ogg", MediaKind.Audio, 1, 65_000, 70_000, "<b>ал</b> үйдө жана ҮЙГӨ"),
                ])));

        var cut = Render<ISC.AI.Modules.Media.UI.CaseMedia>(p => p.Add(x => x.CaseId, 3));

        cut.Markup.ShouldContain("аудио");
        cut.Markup.ShouldNotContain("Переиндексировать (повторно найти лица)");
        cut.Find("input[type=file]").GetAttribute("accept")!.ShouldContain("audio/amr");

        var input = cut.Find("input[maxlength='200']");
        input.Input("ү");
        cut.FindAll("button").First(b => b.TextContent.Contains("Найти")).HasAttribute("disabled").ShouldBeTrue();
        input.Input(" үй ");
        cut.FindAll("button").First(b => b.TextContent.Contains("Найти")).Click();

        _mediator.Received(1).Send(Arg.Is<SearchTranscriptsQuery>(q => q.CaseId == 3 && q.Text == "үй"), Arg.Any<CancellationToken>());
        cut.Markup.ShouldContain("<mark style=\"padding:0;\">үй</mark>");
        cut.Markup.ShouldContain("<mark style=\"padding:0;\">ҮЙ</mark>");
        cut.Markup.ShouldContain("&lt;b&gt;ал&lt;/b&gt;");
        cut.Markup.ShouldContain("voice&lt;1&gt;.ogg");
        cut.Markup.ShouldContain("href=\"/media/assets/7?t=65000\"");
        cut.Markup.ShouldContain("[01:05]");
    }

    [Fact(DisplayName = "Пустой результат поиска не утверждает, что сказанного нет в материалах (ТБ-020/021)")]
    public void CaseMedia_search_empty_result_does_not_claim_absence()
    {
        Render<MudPopoverProvider>();
        _mediator.Send(Arg.Any<ListAccessibleCasesQuery>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<ResponseDto<IReadOnlyList<CaseScopeItem>>>(
                ResponseDto<IReadOnlyList<CaseScopeItem>>.Ok([new CaseScopeItem(3, "12-345", "Дело", 0, 1)])));
        _mediator.Send(Arg.Any<ListCaseMediaQuery>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<ResponseDto<IReadOnlyList<MediaAssetRow>>>(ResponseDto<IReadOnlyList<MediaAssetRow>>.Ok([])));
        _mediator.Send(Arg.Any<SearchTranscriptsQuery>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<ResponseDto<IReadOnlyList<TranscriptHit>>>(ResponseDto<IReadOnlyList<TranscriptHit>>.Ok([])));

        var cut = Render<ISC.AI.Modules.Media.UI.CaseMedia>(p => p.Add(x => x.CaseId, 3));
        var input = cut.Find("input[maxlength='200']");
        input.Input("салам");
        input.KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "Enter" });
        cut.Markup.ShouldContain("либо материалы вне вашего допуска");
    }
}
