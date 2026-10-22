using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Bunit;
using ISC.AI.Abstractions.Application;
using ISC.AI.Modules.Media.Application.Features.Transcripts;
using ISC.AI.Modules.Media.Domain.Model;
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
/// Дикторы в расшифровке на экране (ТФ-МЕД-19, ADR-0062): подпись диктора перед репликой и пометка переноса, абзацы
/// «Текстом» по сменам диктора, полоса «Дикторы» — источник разметки или причина её отсутствия, переименование,
/// объединение и перенос реплики командами; в режиме «только просмотр» правок нет; отказ сервера виден текстом.
/// </summary>
public sealed class TranscriptSpeakersUiTests : BunitContext, IAsyncLifetime
{
    private const string ModulePath = "./_content/ISC.AI.Modules.Media.UI/js/media.js";

    private readonly IMediator _mediator = Substitute.For<IMediator>();
    private readonly List<IRequest<ResponseDto<bool>>> _sent = [];
    private ResponseDto<bool> _answer = ResponseDto<bool>.Ok(true);
    private bool _popovers;

    public TranscriptSpeakersUiTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        JSInterop.SetupModule(ModulePath).Mode = JSRuntimeMode.Loose;
        Services.AddMudServices();
        Services.AddSingleton(_mediator);
        Services.AddSingleton<ISC.AI.Modules.Media.Domain.Services.IMediaLinks>(new ISC.AI.Modules.Media.Application.Features.Navigation.NoMediaLinks());
        Services.AddSingleton(ISC.AI.Modules.Media.Application.MediaUploadOptions.Default);
        Services.AddSingleton(new ISC.AI.Modules.Media.Application.MediaSearchOptions());
        SetRendererInfo(new RendererInfo("Server", true));
        _mediator.Send(Arg.Do<IRequest<ResponseDto<bool>>>(_sent.Add), Arg.Any<CancellationToken>())
            .Returns(_ => new ValueTask<ResponseDto<bool>>(_answer));
    }

    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;

    Task IAsyncLifetime.DisposeAsync() => base.DisposeAsync().AsTask();

    [Fact(DisplayName = "«Текстом»: смена диктора начинает новый абзац с его подписью и без паузы")]
    public void Paragraphs_break_on_speaker_change()
    {
        var paragraphs = TranscriptParagraphs.Group(
        [
            new TranscriptSegmentRow(0, 0, 1_000, "а", Speaker: 1, SpeakerLabel: "объект"),
            new TranscriptSegmentRow(1, 1_100, 2_000, "б", Speaker: 1, SpeakerLabel: "объект"),
            new TranscriptSegmentRow(2, 2_100, 3_000, "в", Speaker: 2, SpeakerLabel: "Диктор 2"),
            new TranscriptSegmentRow(3, 3_100, 4_000, "г", Speaker: 1, SpeakerLabel: "объект"),
        ]);

        paragraphs.Select(p => (p.SpeakerLabel, string.Concat(p.Segments.Select(s => s.Text)))).ShouldBe(
            [("объект", "аб"), ("Диктор 2", "в"), ("объект", "г")]);
        TranscriptSpeakerColors.Of(1).ShouldNotBe(TranscriptSpeakerColors.Of(2));
        TranscriptSpeakerColors.Of(null).ShouldBe("var(--mud-palette-text-secondary)");
    }

    [Fact(DisplayName = "Панель: подпись диктора перед репликой, перенесённая сотрудником — с «✎»; полоса «Дикторы» с числом реплик")]
    public void Panel_shows_speaker_labels()
    {
        SetupTranscript(Speakers(),
            new TranscriptSegmentRow(0, 1_000, 2_000, "салам", Speaker: 1, SpeakerLabel: "объект"),
            new TranscriptSegmentRow(1, 2_500, 3_000, "кандайсың", Speaker: 2, SpeakerLabel: "Диктор 2", SpeakerMoved: true));

        Render<MudPopoverProvider>(); // в полосе — выбор числа говорящих
        var cut = Render<TranscriptPanel>(p => p.Add(x => x.AssetId, 5).Add(x => x.PlayerElementId, "media-audio-5"));

        cut.FindAll(".transcript-speaker").Select(s => s.TextContent.Trim()).ShouldBe(["объект:", "Диктор 2 ✎:"]);
        cut.FindAll(".speaker-chip").Select(c => c.TextContent.Trim()).ShouldBe(["объект · 1", "Диктор 2 · 1"]);
        var bar = cut.Find(".speakers-bar").TextContent;
        bar.ShouldContain("Объединены: Диктор 3 → объект");
        bar.ShouldContain("Дикторы размечены моделью");
    }

    [Theory(DisplayName = "ADR-0062 v0.3: «Дикторы» у «Расшифровать заново» — без дикторов, «авто» или число говорящих уходит в RetranscribeMediaCommand")]
    [InlineData(-1, false, null, "без дикторов")]
    [InlineData(0, true, null, "авто")]
    [InlineData(2, true, 2, "2 говорящих")]
    public async Task Panel_sends_retranscribe_with_speaker_choice(int choice, bool enabled, int? count, string label)
    {
        _mediator.Send(Arg.Any<RetranscribeMediaCommand>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<ResponseDto<Guid>>(ResponseDto<Guid>.Ok(Guid.NewGuid())));
        SetupTranscript(Speakers(), new TranscriptSegmentRow(0, 1_000, 2_000, "салам", Speaker: 1, SpeakerLabel: "объект"));
        Render<MudPopoverProvider>();
        var cut = Render<TranscriptPanel>(p => p.Add(x => x.AssetId, 5).Add(x => x.PlayerElementId, "media-audio-5"));

        await cut.InvokeAsync(() => cut.FindComponent<MudSelect<int>>().Instance.ValueChanged.InvokeAsync(choice));
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Расшифровать заново").Click();

        await _mediator.Received(1).Send(
            Arg.Is<RetranscribeMediaCommand>(c => c.AssetId == 5 && c.Speakers == new SpeakerMarkupRequest(enabled, count)), Arg.Any<CancellationToken>());
        cut.Markup.ShouldContain($"({label})");
    }

    [Fact(DisplayName = "ADR-0062 v0.3: в полосе «Дикторы» выбора числа нет — он у «Расшифровать заново»; только просмотр — нет и там")]
    public void Speaker_choice_only_in_header()
    {
        RenderBar(Transcript(Speakers(), TranscriptSpeakerSource.Model)).FindComponents<MudSelect<int>>().ShouldBeEmpty();

        SetupTranscript(Speakers(), new TranscriptSegmentRow(0, 1_000, 2_000, "салам", Speaker: 1, SpeakerLabel: "объект"));
        var readOnly = Render<TranscriptPanel>(p => p.Add(x => x.AssetId, 5).Add(x => x.PlayerElementId, "media-audio-5").Add(x => x.ReadOnly, true));
        readOnly.FindComponents<MudSelect<int>>().ShouldBeEmpty();
    }

    [Fact(DisplayName = "Полоса: разметки нет — причина и подсказка «Расшифровать заново»; по каналам стерео — своя подпись")]
    public void Bar_shows_source_or_note()
    {
        var none = RenderBar(Transcript([], TranscriptSpeakerSource.None, "запись длиннее 120 мин"));
        none.Find(".speakers-bar").TextContent.ShouldContain("Дикторы не размечены: запись длиннее 120 мин. Чтобы разметить — «Расшифровать заново».");
        none.FindAll(".speaker-chip").ShouldBeEmpty();

        RenderBar(Transcript(Speakers(), TranscriptSpeakerSource.StereoChannels)).Find(".speakers-bar").TextContent
            .ShouldContain("по каналам стерео-записи");
    }

    [Fact(DisplayName = "Переименование: щелчок по диктору — поле имени; «Сохранить имя» — команда с номером и именем, затем перечитывание")]
    public async Task Rename_sends_command_and_reloads()
    {
        var changed = 0;
        var cut = RenderBar(Transcript(Speakers(), TranscriptSpeakerSource.Model), onChanged: () => changed++);

        cut.FindAll(".speaker-chip").Last().Click();
        await cut.InvokeAsync(() => cut.FindComponent<MudTextField<string>>().Instance.ValueChanged.InvokeAsync("свидетель"));
        Button(cut, "Сохранить имя").Click();

        _sent.ShouldHaveSingleItem().ShouldBe(new RenameTranscriptSpeakerCommand(5, 2, "свидетель"));
        changed.ShouldBe(1);
        cut.FindAll("button").ShouldNotContain(b => b.TextContent.Contains("Сохранить имя"));
    }

    [Fact(DisplayName = "Объединение: «Объединить с» — только активные дикторы, кроме самого; команда с обоими номерами")]
    public async Task Merge_sends_command()
    {
        var cut = RenderBar(Transcript(Speakers(), TranscriptSpeakerSource.Model));

        cut.FindAll(".speaker-chip").Last().Click();
        await cut.InvokeAsync(() => cut.FindComponent<MudSelect<int?>>().Instance.ValueChanged.InvokeAsync(1));
        Button(cut, "Объединить").Click();

        _sent.ShouldHaveSingleItem().ShouldBe(new MergeTranscriptSpeakersCommand(5, 2, 1));
    }

    [Fact(DisplayName = "Перенос реплики: выбранная реплика — «Перенести к» другому диктору; перенесённая — «Как разметила система» без диктора")]
    public async Task Move_sends_command()
    {
        var moved = new TranscriptSegmentRow(4, 65_000, 66_000, "жакшы", Speaker: 1, SpeakerLabel: "объект", SpeakerMoved: true);
        var cut = RenderBar(Transcript(Speakers(), TranscriptSpeakerSource.Model), active: moved);

        cut.Find(".speakers-bar").TextContent.ShouldContain("Реплика [01:05] — объект:");
        await cut.InvokeAsync(() => cut.FindComponent<MudSelect<int?>>().Instance.ValueChanged.InvokeAsync(2));
        Button(cut, "Перенести").Click();
        Button(cut, "Как разметила система").Click();

        _sent.ShouldBe([new MoveTranscriptSegmentCommand(5, 4, 2), new MoveTranscriptSegmentCommand(5, 4, null)]);
    }

    [Fact(DisplayName = "Отказ сервера (нет права, объединение по кругу) — предупреждение текстом, перечитывания нет")]
    public async Task Refusal_is_shown()
    {
        _answer = ResponseDto<bool>.BadRequest(TranscriptSpeakerGuard.Denied);
        var changed = 0;
        var cut = RenderBar(Transcript(Speakers(), TranscriptSpeakerSource.Model), onChanged: () => changed++);

        cut.FindAll(".speaker-chip")[0].Click();
        await cut.InvokeAsync(() => cut.FindComponent<MudTextField<string>>().Instance.ValueChanged.InvokeAsync("x"));
        Button(cut, "Сохранить имя").Click();

        cut.Find(".mud-alert").TextContent.ShouldContain(TranscriptSpeakerGuard.Denied);
        changed.ShouldBe(0);
    }

    [Fact(DisplayName = "Только просмотр (материал чужого дела): дикторы видны, правок нет — кнопки дикторов недоступны, переноса нет")]
    public void Read_only_has_no_edits()
    {
        var cut = RenderBar(Transcript(Speakers(), TranscriptSpeakerSource.Model), readOnly: true,
            active: new TranscriptSegmentRow(0, 0, 1_000, "а", Speaker: 1, SpeakerLabel: "объект"));

        cut.FindAll(".speaker-chip").ShouldAllBe(c => c.HasAttribute("disabled"));
        cut.FindComponents<MudTextField<string>>().ShouldBeEmpty();
        cut.FindComponents<MudSelect<int?>>().ShouldBeEmpty();
        cut.FindComponents<MudSelect<int>>().ShouldBeEmpty();
        cut.Find(".speakers-bar").TextContent.ShouldNotContain("Перенести");
        _sent.ShouldBeEmpty();
    }

    // Поставщик всплывающих списков MudBlazor (MudSelect «Объединить с» / «Перенести к») — один на тест.
    private IRenderedComponent<TranscriptSpeakersBar> RenderBar(
        MediaTranscript transcript, bool readOnly = false, TranscriptSegmentRow? active = null, Action? onChanged = null)
    {
        if (!_popovers)
        {
            Render<MudPopoverProvider>();
            _popovers = true;
        }

        return Render<TranscriptSpeakersBar>(p => p
            .Add(x => x.Transcript, transcript)
            .Add(x => x.ReadOnly, readOnly)
            .Add(x => x.ActiveSegment, active)
            .Add(x => x.OnChanged, EventCallback.Factory.Create(this, () => onChanged?.Invoke())));
    }

    private static AngleSharp.Dom.IElement Button(IRenderedComponent<TranscriptSpeakersBar> cut, string text) =>
        cut.FindAll("button").First(b => b.TextContent.Trim() == text);

    // Диктор 1 переименован в «объект», 2 — без имени, 3 слит с 1.
    private static TranscriptSpeakerRow[] Speakers() =>
    [
        new(1, "объект", "объект", null, 1),
        new(2, "Диктор 2", null, null, 1),
        new(3, "Диктор 3", null, 1, 0),
    ];

    private static MediaTranscript Transcript(
        IReadOnlyList<TranscriptSpeakerRow> speakers, TranscriptSpeakerSource source, string? note = null, params TranscriptSegmentRow[] segments) =>
        new(5, TranscriptStatus.Done, null, "gigaam-v3@1", new DateTime(2026, 10, 14, 8, 0, 0, DateTimeKind.Utc), segments,
            speakers, source, note);

    private void SetupTranscript(TranscriptSpeakerRow[] speakers, params TranscriptSegmentRow[] segments) =>
        _mediator.Send(Arg.Any<GetTranscriptQuery>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<ResponseDto<MediaTranscript>>(ResponseDto<MediaTranscript>.Ok(
                Transcript(speakers, TranscriptSpeakerSource.Model, null, segments))));
}
