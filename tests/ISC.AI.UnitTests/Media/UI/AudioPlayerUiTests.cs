using System;
using System.Globalization;
using System.Linq;
using System.Text.Json;
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
/// Проигрыватель аудиозаписи и работа с расшифровкой (ADR-0047, ТФ-МЕД-18) на экране: проигрыватель без браузерных
/// controls отдаёт media.js волну и участки речи, кнопки — команды, состояние приходит обратно; расшифровка — поиск с
/// выделением и переходом, «Следовать за записью», режим «Текстом» абзацами (те же id фрагментов), «Копировать» и
/// «Word» через ExportTranscriptQuery, в режиме «только просмотр» выгрузки нет; карточка аудио — реквизиты строкой,
/// «Удалить» в меню, участки речи из расшифровки доходят до проигрывателя.
/// </summary>
public sealed class AudioPlayerUiTests : BunitContext, IAsyncLifetime
{
    private const string ModulePath = "./_content/ISC.AI.Modules.Media.UI/js/media.js";
    private const string ListId = "media-transcript-list-5";
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private readonly IMediator _mediator = Substitute.For<IMediator>();
    private readonly BunitJSModuleInterop _module;

    public AudioPlayerUiTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        _module = JSInterop.SetupModule(ModulePath);
        _module.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices();
        Services.AddSingleton(_mediator);
        Services.AddSingleton(ISC.AI.Modules.Media.Application.MediaUploadOptions.Default);
        Services.AddSingleton(new ISC.AI.Modules.Media.Application.MediaSearchOptions());
        SetRendererInfo(new RendererInfo("Server", true));
        _mediator.Send(Arg.Any<GetAssetSuggestionStatusQuery>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<ResponseDto<AssetSuggestionStatus>>(ResponseDto<AssetSuggestionStatus>.Ok(
                new AssetSuggestionStatus(true, false, 0.5, [], []))));
        SetupTranscript(
            new TranscriptSegmentRow(0, 1_000, 4_000, "салам дүйнө"),
            new TranscriptSegmentRow(1, 4_500, 7_000, "бир эки"),
            new TranscriptSegmentRow(2, 65_000, 70_000, "дүйнө үч"));
    }

    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;

    Task IAsyncLifetime.DisposeAsync() => base.DisposeAsync().AsTask();

    // ---------------- абзацы ----------------

    [Fact(DisplayName = "«Текстом»: новый абзац после паузы ≥ 2 с или когда абзац длиннее 60 с; порядок — по началу; ни один фрагмент не теряется")]
    public void Paragraphs_split_on_pause_and_length()
    {
        var paragraphs = TranscriptParagraphs.Group(
        [
            new TranscriptSegmentRow(3, 7_000, 8_000, "г"),      // пауза 1 с — тот же абзац
            new TranscriptSegmentRow(0, 0, 1_000, "а"),
            new TranscriptSegmentRow(1, 2_500, 3_000, "б"),      // пауза 1,5 с — тот же абзац
            new TranscriptSegmentRow(2, 5_000, 6_000, "в"),      // пауза 2 с — новый
            new TranscriptSegmentRow(4, 66_000, 67_000, "д"),    // пауза больше 2 с — новый
            new TranscriptSegmentRow(5, 67_500, 68_000, "е"),
        ]);

        paragraphs.Select(p => string.Concat(p.Segments.Select(s => s.Text))).ShouldBe(["аб", "вг", "де"]);
        paragraphs.Select(p => p.StartMs).ShouldBe([0L, 5_000L, 66_000L]);

        // Речь без пауз дольше минуты — абзацы по 60 с.
        var nonstop = TranscriptParagraphs.Group(
            Enumerable.Range(0, 30).Select(i => new TranscriptSegmentRow(i, i * 5_000L, i * 5_000L + 5_000, "x")).ToList());
        nonstop.Count.ShouldBe(3);
        nonstop.Sum(p => p.Segments.Count).ShouldBe(30);
    }

    // ---------------- панель расшифровки ----------------

    [Fact(DisplayName = "Пометка «требует проверки, не протокол» видна всегда одной строкой; модель и дата — в подсказке")]
    public void Panel_notice_is_one_line_with_model_hint()
    {
        var cut = RenderPanel();

        cut.Markup.ShouldContain(TranscriptPanel.NoticeText);
        var hint = cut.Find(".transcript-note span").GetAttribute("title")!;
        hint.ShouldContain("без знаков препинания");
        hint.ShouldContain("gigaam-v3@1");
    }

    [Fact(DisplayName = "Поиск: выделение во фрагментах, «1 из 2», «следующее» — «2 из 2» и прокрутка списка к совпадению; без совпадений — «не найдено»")]
    public async Task Panel_search_highlights_counts_and_steps()
    {
        var cut = RenderPanel();

        await Search(cut, "ДҮЙНӨ");

        cut.FindAll("mark").Select(m => m.TextContent).ShouldBe(["дүйнө", "дүйнө"]);
        cut.Find(".transcript-matches").TextContent.ShouldBe("1 из 2");
        cut.Find("#media-transcript-5-0").GetAttribute("style")!.ShouldContain("outline");
        RevealArgs().ShouldBe(["media-transcript-5-0", ListId, true]);

        cut.Find("button[aria-label='Следующее совпадение']").Click();
        cut.Find(".transcript-matches").TextContent.ShouldBe("2 из 2");
        cut.Find("#media-transcript-5-2").GetAttribute("style")!.ShouldContain("outline");
        RevealArgs().ShouldBe(["media-transcript-5-2", ListId, true]);

        await Search(cut, "жок");
        cut.Find(".transcript-matches").TextContent.ShouldBe("не найдено");
        cut.FindAll("mark").ShouldBeEmpty();
    }

    [Fact(DisplayName = "«Текстом»: абзацы с таймкодом начала, фрагменты — кнопки с теми же id; щелчок воспроизводит с начала фрагмента")]
    public void Panel_text_view_renders_paragraphs_with_same_ids()
    {
        var cut = RenderPanel();

        cut.FindAll(".mud-toggle-item").Single(b => b.TextContent.Contains("Текстом")).Click();

        cut.FindComponents<TranscriptParagraphItem>().Count.ShouldBe(2); // пауза 0,5 с — вместе; 58 с — отдельно
        var segment = cut.Find("#media-transcript-5-1");
        segment.TagName.ShouldBe("BUTTON");
        segment.ClassList.ShouldContain("tp-seg");
        cut.FindAll(".tp .transcript-time").Select(t => t.TextContent).ShouldBe(["[00:01]", "[01:05]"]);

        segment.Click();
        var seek = _module.Invocations["seek"].Last();
        seek.Arguments[0].ShouldBe("media-audio-5");
        seek.Arguments[1].ShouldBe(4.5);
        seek.Arguments[2].ShouldBe(true);
    }

    [Fact(DisplayName = "Следование за записью: панель привязывается к проигрывателю; смена фрагмента — подсветка и прокрутка списка; «Следовать» выключено — только подсветка")]
    public async Task Panel_follows_playback()
    {
        var cut = RenderPanel();

        var follow = _module.Invocations["followTranscript"].ShouldHaveSingleItem();
        follow.Arguments[0].ShouldBe("media-audio-5");
        follow.Arguments[1].ShouldBe(ListId);
        JsonSerializer.Serialize(follow.Arguments[2], Web).ShouldBe("[[0,1000,4000],[1,4500,7000],[2,65000,70000]]");

        await cut.InvokeAsync(() => cut.Instance.OnPlaybackSegment(1));
        cut.Find("#media-transcript-5-1").GetAttribute("style")!.ShouldContain("var(--mud-palette-primary)");
        RevealArgs().ShouldBe(["media-transcript-5-1", ListId, false]);

        cut.Find("input[type='checkbox']").Change(false); // «Следовать за записью»
        var reveals = _module.Invocations["revealInList"].Count;
        await cut.InvokeAsync(() => cut.Instance.OnPlaybackSegment(2));
        cut.Find("#media-transcript-5-2").GetAttribute("style")!.ShouldContain("var(--mud-palette-primary)");
        _module.Invocations["revealInList"].Count.ShouldBe(reveals);
    }

    [Fact(DisplayName = "«Копировать»: ExportTranscriptQuery в формате «текст» (выгрузка в журнале) и copyText с этим текстом; итог — сообщение")]
    public void Panel_copy_exports_text_and_copies()
    {
        ExportTranscriptQuery? sent = null;
        _mediator.Send(Arg.Do<ExportTranscriptQuery>(q => sent = q), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<ResponseDto<TranscriptExport>>(ResponseDto<TranscriptExport>.Ok(
                new TranscriptExport("Расшифровка носителя 5.txt", "text/plain", [], "СЕКРЕТНО\r\n…\r\nСЕКРЕТНО"))));
        _module.Setup<bool>("copyText", _ => true).SetResult(true);
        var cut = RenderPanel();

        cut.Find("button[aria-label='Копировать текст']").Click();

        sent.ShouldNotBeNull().Format.ShouldBe(TranscriptExportFormat.Text);
        _module.Invocations["copyText"].ShouldHaveSingleItem().Arguments[0].ShouldBe("СЕКРЕТНО\r\n…\r\nСЕКРЕТНО");
        cut.Markup.ShouldContain("Текст расшифровки скопирован");
    }

    [Fact(DisplayName = "«Word»: ExportTranscriptQuery в формате docx и отдача файла браузеру; отказ сервера — показан как есть")]
    public void Panel_word_downloads_document_or_shows_refusal()
    {
        _mediator.Send(Arg.Any<ExportTranscriptQuery>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<ResponseDto<TranscriptExport>>(ResponseDto<TranscriptExport>.Ok(
                new TranscriptExport("Расшифровка носителя 5.docx", "application/x", [1, 2, 3], null))));
        var cut = RenderPanel();

        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Word").Click();

        JSInterop.Invocations["downloadFileFromStream"].ShouldHaveSingleItem().Arguments[0].ShouldBe("Расшифровка носителя 5.docx");

        _mediator.Send(Arg.Any<ExportTranscriptQuery>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<ResponseDto<TranscriptExport>>(ResponseDto<TranscriptExport>.BadRequest(ExportTranscriptQuery.ViewOnlyMessage)));
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Word").Click();
        cut.Markup.ShouldContain(ExportTranscriptQuery.ViewOnlyMessage);
    }

    [Fact(DisplayName = "Только просмотр (материал чужого дела, ADR-0044): ни «Копировать», ни «Word», ни повторной расшифровки; поиск и «Текстом» есть")]
    public void Panel_read_only_has_no_export()
    {
        var cut = RenderPanel(readOnly: true);

        cut.FindAll("button[aria-label='Копировать текст']").ShouldBeEmpty();
        cut.FindAll("button").Where(b => b.TextContent.Trim() == "Word").ShouldBeEmpty();
        cut.Markup.ShouldNotContain("Расшифровать заново");
        cut.Markup.ShouldContain("Найти в расшифровке");
        cut.Markup.ShouldContain("Текстом");
    }

    // ---------------- проигрыватель ----------------

    [Fact(DisplayName = "Проигрыватель: <audio> без браузерных controls; media.js получает волну, длительность и участки речи; кнопки — команды")]
    public void Player_initializes_js_and_sends_commands()
    {
        var cut = Render<AudioPlayer>(p => p
            .Add(x => x.AssetId, 5)
            .Add(x => x.Src, "/media/files/media-originals/5/a.mp3")
            .Add(x => x.ElementId, "media-audio-5")
            .Add(x => x.WaveformUrl, MediaFileUrl.Waveform(5))
            .Add(x => x.DurationMs, 70_000L)
            .Add(x => x.Segments, [new TranscriptSegmentRow(0, 1_000, 4_000, "салам")])
            .Add(x => x.AllowDownload, true)
            .Add(x => x.DownloadName, "допрос.mp3"));

        var audio = cut.Find("audio#media-audio-5");
        audio.HasAttribute("controls").ShouldBeFalse();
        audio.GetAttribute("preload").ShouldBe("metadata");

        var init = _module.Invocations["initAudioPlayer"].ShouldHaveSingleItem();
        init.Arguments[0].ShouldBe("media-audio-player-5");
        var options = JsonSerializer.Serialize(init.Arguments[1], Web);
        options.ShouldContain("\"elementId\":\"media-audio-5\"");
        options.ShouldContain("\"waveformUrl\":\"/media/waveforms/5\"");
        options.ShouldContain("\"durationMs\":70000");
        options.ShouldContain("\"ranges\":[[1000,4000]]");

        cut.Find("button[aria-label='Воспроизвести']").Click();
        cut.Find("button[aria-label='Назад на 5 секунд']").Click();
        cut.FindAll(".mud-toggle-item").Single(b => b.TextContent.Trim() == "1,5×").Click();
        cut.Find("button[aria-label='Выключить звук']").Click();

        var commands = _module.Invocations["audioCommand"]
            .Select(i => string.Create(CultureInfo.InvariantCulture, $"{i.Arguments[1]}:{i.Arguments[2]}")).ToList();
        commands.ShouldBe(["toggle:0", "skip:-5", "rate:1.5", "mute:0"]);
        cut.Find("a[aria-label='Скачать оригинал']").GetAttribute("download").ShouldBe("допрос.mp3");
    }

    [Fact(DisplayName = "Состояние от media.js: «играет» — кнопка «Пауза», скорость 1,25× выбрана, звук выключен; без права скачивания — ссылки нет")]
    public async Task Player_reflects_state_from_js()
    {
        var cut = Render<AudioPlayer>(p => p
            .Add(x => x.AssetId, 5)
            .Add(x => x.Src, "/media/files/media-originals/5/a.mp3")
            .Add(x => x.ElementId, "media-audio-5"));

        await cut.InvokeAsync(() => cut.Instance.OnPlayerState(true, 1.25, true));

        cut.FindAll("button[aria-label='Пауза']").Count.ShouldBe(1);
        cut.FindAll("button[aria-label='Включить звук']").Count.ShouldBe(1);
        cut.FindAll(".mud-toggle-item").Single(b => b.TextContent.Trim() == "1,25×").ClassList.ShouldContain("mud-toggle-item-selected");
        cut.FindAll("a[aria-label='Скачать оригинал']").ShouldBeEmpty();
        AudioPlayer.RateLabel(0.75).ShouldBe("0,75×");
        AudioPlayer.RateLabel(2).ShouldBe("2×");
    }

    // ---------------- карточка аудио ----------------

    [Fact(DisplayName = "Карточка аудио: проигрыватель с волной, реквизиты строкой (без блока «Реквизиты»), «Удалить» — в меню «⋯», участки речи из расшифровки — в проигрыватель")]
    public void AssetCard_audio_uses_new_player_and_inline_requisites()
    {
        var popovers = Render<MudPopoverProvider>();
        _mediator.Send(Arg.Any<GetMediaAssetQuery>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<ResponseDto<MediaAssetDetails>>(ResponseDto<MediaAssetDetails>.Ok(
                new MediaAssetDetails(Asset(), [], 3))));

        var cut = Render<AssetCard>(p => p.Add(x => x.Id, 5));

        cut.WaitForAssertion(() => cut.FindComponents<AudioPlayer>().Count.ShouldBe(1));
        cut.Find("audio#media-audio-5").HasAttribute("controls").ShouldBeFalse();
        cut.Markup.ShouldNotContain(">Реквизиты<");
        cut.Markup.ShouldContain("Дело:");
        cut.FindAll("button").Where(b => b.TextContent.Trim() == "Удалить").ShouldBeEmpty();
        cut.Find("button[aria-label='Ещё действия']").Click();
        popovers.WaitForAssertion(() => popovers.Markup.ShouldContain("Удалить носитель…")); // меню рисует поставщик всплывающих

        // Фрагменты расшифровки дошли до проигрывателя: последняя передача настроек — с тремя участками речи.
        cut.WaitForAssertion(() =>
        {
            var calls = _module.Invocations.Where(i => i.Identifier is "initAudioPlayer" or "updateAudioPlayer").ToList();
            calls.ShouldNotBeEmpty();
            JsonSerializer.Serialize(calls[^1].Arguments[1], Web).ShouldContain("\"ranges\":[[1000,4000],[4500,7000],[65000,70000]]");
        });
    }

    // ---------------- обвязка ----------------

    private IRenderedComponent<TranscriptPanel> RenderPanel(bool readOnly = false) =>
        Render<TranscriptPanel>(p => p
            .Add(x => x.AssetId, 5)
            .Add(x => x.PlayerElementId, "media-audio-5")
            .Add(x => x.ReadOnly, readOnly));

    private static Task Search(IRenderedComponent<TranscriptPanel> cut, string text) =>
        cut.InvokeAsync(() => cut.FindComponent<MudTextField<string>>().Instance.ValueChanged.InvokeAsync(text));

    private object?[] RevealArgs() => _module.Invocations["revealInList"].Last().Arguments.ToArray();

    private void SetupTranscript(params TranscriptSegmentRow[] segments) =>
        _mediator.Send(Arg.Any<GetTranscriptQuery>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<ResponseDto<MediaTranscript>>(ResponseDto<MediaTranscript>.Ok(
                new MediaTranscript(5, TranscriptStatus.Done, null, "gigaam-v3@1",
                    new DateTime(2026, 10, 14, 8, 0, 0, DateTimeKind.Utc), segments))));

    private static MediaAssetRow Asset() => new(
        5, MediaKind.Audio, "допрос.mp3", "0123456789abcdef0123456789abcdef.mp3", "audio/mpeg", 1024, 70_000, null, null,
        0, 1, 1, MediaIndexStatus.NotApplicable, null, null, null, null, DateTime.UtcNow, 0, TranscriptStatus.Done);
}
