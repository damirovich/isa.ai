using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Bunit;
using ISC.AI.Abstractions.Application;
using ISC.AI.Modules.Media.Application.Features.Assets;
using ISC.AI.Modules.Media.Application.Features.Suggestions;
using ISC.AI.Modules.Media.Application.Features.Transcripts;
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
using Xunit;

namespace ISC.AI.UnitTests.Media.UI;

/// <summary>
/// Лента под видео (ADR-0038, ТФ-МЕД-11): разметка (деления, лента кадров или пояснение, дорожки отметок, легенда),
/// переход по отметке и с ленты, слежение бегунка за кадром с сервера; на карточке носителя — дорожки фигурантов и
/// треков, время записи из файла и «применить как дату съёмки».
/// </summary>
public sealed class VideoTimelineComponentTests : BunitContext, IAsyncLifetime
{
    private const string ModulePath = "./_content/ISC.AI.Modules.Media.UI/js/media.js";
    private const string Strip = "0123456789abcdef0123456789abcdef.jpg";
    private readonly IMediator _mediator = Substitute.For<IMediator>();
    private readonly BunitJSModuleInterop _module;

    public VideoTimelineComponentTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        _module = JSInterop.SetupModule(ModulePath);
        _module.Mode = JSRuntimeMode.Loose;
        _module.Setup<bool>("initTimeline", _ => true).SetResult(true);
        _module.Setup<bool>("updateTimeline", _ => true).SetResult(true);
        Services.AddMudServices();
        Services.AddSingleton(_mediator);
        SetRendererInfo(new RendererInfo("Server", true));

        _mediator.Send(Arg.Any<GetAssetSuggestionStatusQuery>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<ResponseDto<AssetSuggestionStatus>>(ResponseDto<AssetSuggestionStatus>.Ok(
                new AssetSuggestionStatus(true, false, 0.5, [], []))));
        _mediator.Send(Arg.Any<GetTranscriptQuery>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<ResponseDto<MediaTranscript>>(ResponseDto<MediaTranscript>.Ok(
                new MediaTranscript(5, TranscriptStatus.NotApplicable, null, null, null, []))));
    }

    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;

    Task IAsyncLifetime.DisposeAsync() => base.DisposeAsync().AsTask();

    private static readonly IReadOnlyList<TimelineLane> Lanes =
    [
        new("person-1", "Иванов И.И.", "#e53935", [new TimelineSpan(10_000, 20_000, "Иванов И.И. · 0:10.0 – 0:20.0")]),
        new("person-2", "Петров П.П.", "#1e88e5", [new TimelineSpan(5_000, 5_000, "Петров П.П. · 0:05.0")]),
    ];

    private IRenderedComponent<VideoTimeline> RenderTimeline(
        string? filmstripUrl = "/media/files/media-filmstrips/5/" + Strip, Action<long>? onSeek = null, bool playable = false,
        RecordClock? clock = null, string? hint = null) =>
        Render<VideoTimeline>(p => p
            .Add(x => x.AssetId, 5)
            .Add(x => x.DurationMs, 40_000)
            .Add(x => x.Playable, playable)
            .Add(x => x.VideoElementId, "media-video-5")
            .Add(x => x.FilmstripUrl, filmstripUrl)
            .Add(x => x.FilmstripTileCount, 40)
            .Add(x => x.FilmstripStepMs, 1_000)
            .Add(x => x.Clock, clock)
            .Add(x => x.FilmstripHint, hint)
            .Add(x => x.Lanes, Lanes)
            .Add(x => x.OnSeek, EventCallback.Factory.Create<long>(this, ms => onSeek?.Invoke(ms))));

    [Fact(DisplayName = "Разметка: деления с подписями, контейнер ленты кадров, отметки процентами, легенда; media.js получает настройки")]
    public void Renders_ruler_strip_marks_and_initializes_js()
    {
        var cut = RenderTimeline(clock: new RecordClock(1_670_508_492_000, Approximate: false));

        cut.FindAll(".vt-tick").Count.ShouldBe(8); // шаг 5 с на 40 с
        cut.Markup.ShouldContain("00:05");
        cut.Find("[data-vt-strip]");
        cut.Markup.ShouldNotContain("Ленты кадров у этого видео нет");

        var marks = cut.FindAll("[data-vt-mark]");
        marks.Count.ShouldBe(2);

        // Фигурант — плашка цветом своей дорожки с именем внутри; точка (один кадр) — с нулевой шириной (минимум в стилях).
        var person = marks[0].GetAttribute("style")!;
        person.ShouldContain("left:25%;width:25%;");
        person.ShouldContain("background:#e5393559;border-color:#e53935;");
        marks[0].GetAttribute("title").ShouldBe("Иванов И.И. · 0:10.0 – 0:20.0");
        marks[0].TextContent.ShouldContain("Иванов И.И.");
        marks[1].GetAttribute("style")!.ShouldContain("left:12.5%;width:0%;");
        marks[1].TextContent.ShouldContain("Петров П.П.");
        cut.FindAll("img").ShouldBeEmpty();
        cut.Find(".vt-legend").TextContent.ShouldContain("Иванов И.И.");
        cut.Find(".vt-legend").TextContent.ShouldContain("Петров П.П.");
        cut.Find("[data-vt-record]").GetAttribute("title")!.ShouldContain("По метаданным файла");

        var init = _module.Invocations["initTimeline"].ShouldHaveSingleItem();
        init.Arguments[0].ShouldBe("media-timeline-5");
        var options = init.Arguments[1]!.ToString()!;
        options.ShouldContain("DurationMs = 40000");
        options.ShouldContain("FilmstripUrl = /media/files/media-filmstrips/5/" + Strip);
        options.ShouldContain("TileCount = 40");
        options.ShouldContain("RecordStartMs = 1670508492000");
    }

    [Fact(DisplayName = "Ленты кадров нет — пояснение вместо неё (общее или переданное), без контейнера для плиток")]
    public void Without_filmstrip_shows_hint()
    {
        var cut = RenderTimeline(filmstripUrl: null);
        cut.FindAll("[data-vt-strip]").ShouldBeEmpty();
        cut.Markup.ShouldContain("она появится после «Переиндексировать»");

        var processing = RenderTimeline(filmstripUrl: null, hint: "Лента кадров появится, когда видео будет обработано.");
        processing.Markup.ShouldContain("когда видео будет обработано");
        processing.Find(".vt-clock");
        processing.FindAll("[data-vt-record]").ShouldBeEmpty(); // часов записи нет — и места под них нет
    }

    [Fact(DisplayName = "Щелчок по отметке — переход к её началу; переход с ленты (media.js) — к моменту, зажатому в запись")]
    public async Task Mark_click_and_strip_seek()
    {
        var seeks = new List<long>();
        var cut = RenderTimeline(onSeek: seeks.Add);

        cut.FindAll("[data-vt-mark]")[0].Click();
        cut.FindAll("[data-vt-mark]")[1].Click();
        await cut.InvokeAsync(() => cut.Instance.OnTimelineSeek(25_000));
        await cut.InvokeAsync(() => cut.Instance.OnTimelineSeek(90_000));

        seeks.ShouldBe([10_000, 5_000, 25_000, 40_000]);
    }

    [Fact(DisplayName = "Кадры с сервера: смена показанного момента двигает бегунок; в режиме <video> бегунок ведёт сам браузер")]
    public void Server_mode_follows_current_moment()
    {
        var cut = RenderTimeline();

        cut.Render(p => p.Add(x => x.CurrentMs, 12_000));

        var call = _module.Invocations["setTimelinePosition"].ShouldHaveSingleItem();
        call.Arguments[1].ShouldBe(12_000L);

        var playable = RenderTimeline(playable: true);
        playable.Render(p => p.Add(x => x.CurrentMs, 3_000));
        _module.Invocations["setTimelinePosition"].Count.ShouldBe(1);
    }

    private static MediaAssetRow Video(DateTimeOffset? capturedAt, DateTimeOffset? recordedAt) => new(
        5, MediaKind.Video, "v.mkv", "0123456789abcdef0123456789abcdef.mkv", "video/x-matroska", 1024, 40_000, null, capturedAt,
        0, 1, 1, MediaIndexStatus.Indexed, null, null, null, null, DateTime.UtcNow, 0, TranscriptStatus.NotApplicable,
        25, 1280, 720, null, null, Strip, 40, 1_000, recordedAt);

    private bool _popoverRendered;

    private IRenderedComponent<AssetCard> RenderCard(MediaAssetRow asset, IReadOnlyList<FaceRow> faces, IReadOnlyList<AssetPersonSpan> persons)
    {
        // Поставщик всплывающих окон MudBlazor — один на тест (второй экземпляр конфликтует с первым).
        if (!_popoverRendered)
        {
            Render<MudPopoverProvider>();
            _popoverRendered = true;
        }

        _mediator.Send(Arg.Any<GetMediaAssetQuery>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<ResponseDto<MediaAssetDetails>>(ResponseDto<MediaAssetDetails>.Ok(
                new MediaAssetDetails(asset, faces, 3, persons))));
        return Render<AssetCard>(p => p.Add(x => x.Id, 5));
    }

    private static FaceRow TrackFace(int id, long timestampMs, int track) =>
        new(id, 5, (int)(timestampMs / 40), timestampMs, 10, 10, 50, 50, 0.9f, 0.9f, true, null, null, track, 0, 1);

    [Fact(DisplayName = "Карточка видео: под просмотрщиком лента — ссылка на ленту кадров и дорожка фигуранта; треков лиц на ленте нет (они карточками ниже)")]
    public void Asset_card_builds_lanes()
    {
        var cut = RenderCard(
            Video(capturedAt: null, recordedAt: null),
            [TrackFace(11, 1_000, 1), TrackFace(12, 3_000, 1), TrackFace(13, 30_000, 2)],
            [new AssetPersonSpan(7, "Иванов И.И.", 1_000, 3_000)]);

        cut.Find("#media-timeline-5");
        var legend = cut.Find(".vt-legend").TextContent;
        legend.ShouldContain("Иванов И.И.");
        legend.ShouldNotContain("Найденные лица");
        var marks = cut.FindAll("[data-vt-mark]");
        marks.ShouldHaveSingleItem().GetAttribute("title").ShouldBe("Иванов И.И. · 0:01.0 – 0:03.0");
        cut.Markup.ShouldContain("Трек 2"); // карточки треков под видео остаются

        var options = _module.Invocations["initTimeline"].Last().Arguments[1]!.ToString()!;
        options.ShouldContain("FilmstripUrl = /media/files/media-filmstrips/5/" + Strip);
        options.ShouldContain("RecordStartMs = ,"); // времени записи нет ни в файле, ни в реквизитах

        // Щелчок по отметке фигуранта — кадр с сервера в начале отрезка (MKV показывается кадрами).
        marks[0].Click();
        cut.Find("img#media-frame-5").GetAttribute("src").ShouldBe("/media/frames/5?t=1000");
    }

    [Fact(DisplayName = "Время записи из файла: показано в реквизитах; даты съёмки нет — «применить» записывает её аудируемой командой")]
    public void Recorded_time_can_be_applied()
    {
        var recorded = new DateTimeOffset(2022, 12, 8, 8, 8, 12, TimeSpan.Zero);
        _mediator.Send(Arg.Any<SetAssetCapturedAtCommand>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<ResponseDto<bool>>(ResponseDto<bool>.Ok(true)));
        var cut = RenderCard(Video(capturedAt: null, recordedAt: recorded), [], []);

        cut.Markup.ShouldContain("В файле: " + recorded.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture));
        cut.Find("[data-vt-record]").GetAttribute("title")!.ShouldContain("По метаданным файла");

        cut.FindAll("a, button").First(e => e.TextContent.Contains("применить как дату съёмки")).Click();

        _mediator.Received(1).Send(
            Arg.Is<SetAssetCapturedAtCommand>(c => c.AssetId == 5 && c.CapturedAt == recorded),
            Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "Дата съёмки расходится с временем из файла больше чем на минуту — пометка; совпадает — без пометки и без «применить»")]
    public void Recorded_time_mismatch_is_flagged()
    {
        var recorded = new DateTimeOffset(2022, 12, 8, 8, 8, 12, TimeSpan.Zero);

        var differs = RenderCard(Video(capturedAt: recorded.AddMinutes(-5), recordedAt: recorded), [], []);
        differs.Markup.ShouldContain("не совпадает с датой съёмки");
        differs.Markup.ShouldNotContain("применить как дату съёмки");

        var same = RenderCard(Video(capturedAt: recorded.AddSeconds(-12), recordedAt: recorded), [], []);
        same.Markup.ShouldNotContain("не совпадает с датой съёмки");
    }
}
