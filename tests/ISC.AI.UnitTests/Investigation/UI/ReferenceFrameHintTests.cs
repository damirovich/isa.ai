using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Bunit;
using ISC.AI.Abstractions.Application;
using ISC.AI.Modules.Media.Application.Features.Assets;
using ISC.AI.Modules.Media.Domain.Model;
using ISC.AI.Modules.Media.Domain.Services;
using ISC.AI.Profile.Investigation.Application.Features.Persons;
using ISC.AI.Profile.Investigation.UI;
using Mediator;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using NSubstitute;
using Shouldly;
using Xunit;

namespace ISC.AI.UnitTests.Investigation.UI;

/// <summary>
/// Подсказка лучшего кадра (ТФ-МЕД-21, ADR-0058): блок на вкладке «Эталоны» — вырезка, оценки, сравнение с действующим
/// эталоном, «Сделать эталоном…» отдаёт кадр карточке; без кадров блока нет. Окно «Новый эталон» с подсказанным кадром
/// выбирает его само — как мышкой, решение («Добавить») остаётся за человеком (ТБ-077).
/// </summary>
public sealed class ReferenceFrameHintTests : BunitContext, IAsyncLifetime
{
    private readonly IMediator _mediator = Substitute.For<IMediator>();

    public ReferenceFrameHintTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices();
        Services.AddSingleton(_mediator);
        Services.AddSingleton(Substitute.For<IImageTools>());
        SetRendererInfo(new RendererInfo("Server", true));
    }

    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;

    Task IAsyncLifetime.DisposeAsync() => base.DisposeAsync().AsTask();

    [Fact(DisplayName = "Подсказка: вырезка, оценка и частные оценки, «лучше действующего эталона»; «Сделать эталоном…» отдаёт кадр")]
    public void Hint_shows_metrics_and_offers_frame()
    {
        var best = new ReferenceFrameCandidate(10, 101, "рынок.mp4", 65_000, "crop101.jpg", new FrameMetrics(0.82f, 0.75f, 5, 3, 0, 64, 0.81f));
        var current = new ReferenceFrameCandidate(10, 100, "рынок.mp4", 1_000, "crop100.jpg", new FrameMetrics(0.4f, 0.6f, 25, 0, 0, 40, 0.45f));
        Respond(new ReferenceFrameSuggestion(best, current, 4, SuggestedIsBetter: true));
        ReferenceFrameCandidate? used = null;

        var cut = Render<ReferenceFrameHint>(p => p
            .Add(x => x.PersonId, 7)
            .Add(x => x.Use, (ReferenceFrameCandidate c) => used = c));

        cut.WaitForAssertion(() => cut.Markup.ShouldContain("Подсказка: лучший кадр для эталона"));
        cut.Markup.ShouldContain("Носитель № 10 «рынок.mp4» · кадр 01:05");
        cut.Markup.ShouldContain("оценка кадра 81 %");
        cut.Markup.ShouldContain("резкость 82 %");
        cut.Markup.ShouldContain("Лучше действующего эталона (оценка кадра 45 %).");
        cut.Markup.ShouldContain("не вероятность тождества");
        cut.Find("img").GetAttribute("src").ShouldNotBeNull().ShouldContain("crop101.jpg");

        cut.FindAll("button").Single(b => b.TextContent.Contains("Сделать эталоном", StringComparison.Ordinal)).Click();
        used.ShouldBe(best);
        _mediator.Received(1).Send(Arg.Is<SuggestReferenceFrameQuery>(q => q.PersonId == 7), Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "Подсказка: действующий эталон не хуже — так и сказано; кадров нет — блока нет")]
    public void Hint_comparison_and_empty()
    {
        var best = new ReferenceFrameCandidate(10, 102, "рынок.mp4", null, null, new FrameMetrics(0.5f, 0.5f, 0, 0, 0, 60, 0.6f));
        var current = new ReferenceFrameCandidate(10, 101, "рынок.mp4", null, null, new FrameMetrics(0.8f, 0.8f, 0, 0, 0, 80, 0.8f));
        Respond(new ReferenceFrameSuggestion(best, current, 3, SuggestedIsBetter: false));
        var worse = Render<ReferenceFrameHint>(p => p.Add(x => x.PersonId, 7));
        worse.WaitForAssertion(() => worse.Markup.ShouldContain("Действующий эталон не хуже (оценка кадра 80 %) — замена не нужна."));

        Respond(new ReferenceFrameSuggestion(null, null, 0, SuggestedIsBetter: false));
        var empty = Render<ReferenceFrameHint>(p => p.Add(x => x.PersonId, 8));
        empty.Markup.Trim().ShouldBeEmpty();
    }

    [Fact(DisplayName = "Окно эталона с подсказанным кадром: носитель и лицо выбраны сами (как мышкой)")]
    public void Picker_selects_initial_frame()
    {
        var video = new MediaAssetRow(10, MediaKind.Video, "рынок.mp4", "a.mp4", "video/mp4", 1, 1000, null, null,
            1, 5, 1, MediaIndexStatus.Indexed, null, null, null, null, DateTime.UtcNow, 2);
        _mediator.Send(Arg.Any<ListCaseMediaQuery>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<ResponseDto<IReadOnlyList<MediaAssetRow>>>(ResponseDto<IReadOnlyList<MediaAssetRow>>.Ok([video])));
        _mediator.Send(Arg.Any<GetMediaAssetQuery>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<ResponseDto<MediaAssetDetails>>(ResponseDto<MediaAssetDetails>.Ok(new MediaAssetDetails(video,
            [
                new FaceRow(100, 10, 1, 1_000, 0, 0, 50, 50, 0.9f, 0.9f, true, null, "c100.jpg", 1, 1, 5),
                new FaceRow(101, 10, 2, 2_000, 0, 0, 50, 50, 0.9f, 0.9f, true, null, "c101.jpg", 1, 1, 5),
            ], 3))));
        ReferencePick? picked = null;

        Render<MudPopoverProvider>();
        var cut = Render<ReferencePhotoPicker>(p => p
            .Add(x => x.CaseId, 3)
            .Add(x => x.Initial, new ReferencePick(10, 101))
            .Add(x => x.SelectionChanged, (ReferencePick? pick) => picked = pick));

        cut.WaitForState(() => picked is not null);
        picked.ShouldBe(new ReferencePick(10, 101));
    }

    private void Respond(ReferenceFrameSuggestion suggestion) =>
        _mediator.Send(Arg.Any<SuggestReferenceFrameQuery>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<ResponseDto<ReferenceFrameSuggestion>>(ResponseDto<ReferenceFrameSuggestion>.Ok(suggestion)));
}
