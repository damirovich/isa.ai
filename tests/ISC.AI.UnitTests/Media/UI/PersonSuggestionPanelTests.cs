using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Bunit;
using ISC.AI.Abstractions.Application;
using ISC.AI.Modules.Media.Application.Features.Suggestions;
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

// CA2012: здесь ValueTask-методы IMediator только настраиваются и проверяются подменой NSubstitute, а не исполняются.
#pragma warning disable CA2012

namespace ISC.AI.UnitTests.Media.UI;

/// <summary>
/// Блок «Сверка с фигурантами дела» на карточке носителя (ТФ-ПЕР-09, ADR-0035): объясняет, почему сверки не было,
/// показывает предложения системы со ссылкой на нужную стадию верификации, не называя фигуранта, и запускает сверку
/// кнопкой только при праве.
/// </summary>
public sealed class PersonSuggestionPanelTests : BunitContext, IAsyncLifetime
{
    private const int AssetId = 31;

    private static readonly DateTime RunAt = new(2026, 10, 1, 8, 28, 0, DateTimeKind.Utc);

    private readonly IMediator _mediator = Substitute.For<IMediator>();

    public PersonSuggestionPanelTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices();
        Services.AddSingleton(_mediator);
        SetRendererInfo(new RendererInfo("Server", true));
    }

    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;

    Task IAsyncLifetime.DisposeAsync() => base.DisposeAsync().AsTask();

    [Fact(DisplayName = "Нет эталонов и нет основания — блок объясняет причину и что сделать; кнопки «Сверить сейчас» нет")]
    public void Explains_why_not_checked()
    {
        Setup(new AssetSuggestionStatus(true, false, 0.5,
        [
            new CaseSuggestionStatus(7, "В-3/26", CaseSuggestionState.NoReferences, 0, null),
            new CaseSuggestionStatus(9, "Т-1", CaseSuggestionState.NoBasis, 2, null),
        ], []));

        var cut = RenderPanel();

        cut.Markup.ShouldContain("Дело В-3/26");
        cut.Markup.ShouldContain("у фигурантов нет эталонов");
        cut.Markup.ShouldContain("Добавьте эталон в карточке фигуранта");
        cut.Markup.ShouldContain("нет действующего основания поиска");
        cut.Markup.ShouldContain("совпадения от 50 %");
        cut.FindAll("button").Any(b => b.TextContent.Contains("Сверить сейчас", StringComparison.Ordinal)).ShouldBeFalse();
    }

    [Fact(DisplayName = "Предложение системы: строка с лицом, сходством и стадией; «Открыть» ведёт эксперта к решению с фигурантом; фигурант не назван")]
    public void Shows_suggestion_with_expert_link()
    {
        Setup(new AssetSuggestionStatus(true, true, 0.5,
            [new CaseSuggestionStatus(7, "В-3/26", CaseSuggestionState.Checked, 1, Run(candidates: 1))],
            [new SuggestedCandidateRow(945, 92, 7, 74, 0.65, CandidateStatus.Candidate, 11)]));

        var cut = RenderPanel();

        cut.Markup.ShouldContain("Предложено системой: 1");
        cut.Markup.ShouldContain("Лицо № 74</b> похоже на эталон фигуранта");
        cut.Markup.ShouldNotContain("(дело В-3/26)"); // одно дело — номер в строке не повторяется
        cut.Markup.ShouldContain("схожесть 65%");
        cut.Markup.ShouldContain(CandidateStatus.Candidate.Label());
        cut.Markup.ShouldContain("Предложено кандидатов: 1.");
        cut.FindAll("a").Single(a => a.GetAttribute("href")!.StartsWith("/media/verification/945", StringComparison.Ordinal)).TextContent.ShouldContain("К решению эксперта");
        cut.FindAll("a").Select(a => a.GetAttribute("href")).ShouldContain("/media/verification/945?stage=Expert&person=11");
        cut.FindAll("button").Any(b => b.TextContent.Contains("Сверить сейчас", StringComparison.Ordinal)).ShouldBeTrue();
    }

    [Fact(DisplayName = "Кандидат ждёт верификатора — ссылка на слепую проекцию без фигуранта; решённый — на сессию")]
    public void Links_follow_candidate_stage()
    {
        Setup(new AssetSuggestionStatus(true, false, 0.5,
            [new CaseSuggestionStatus(7, "В-3/26", CaseSuggestionState.Checked, 1, Run(candidates: 0))],
            [
                new SuggestedCandidateRow(945, 92, 7, 74, 0.65, CandidateStatus.PendingVerifier, null),
                new SuggestedCandidateRow(946, 93, 7, 75, 0.55, CandidateStatus.Confirmed, null),
            ]));

        var hrefs = RenderPanel().FindAll("a").Select(a => a.GetAttribute("href")).ToList();

        hrefs.ShouldContain("/media/verification/945?stage=Verifier");
        hrefs.ShouldContain("/media/sessions/93");
        hrefs.ShouldNotContain(h => h != null && h.Contains("person=", StringComparison.Ordinal));
    }

    [Fact(DisplayName = "Не сверялся — кнопка «Сверить сейчас» ставит сверку и сообщает, что итог появится здесь")]
    public void Request_button_sends_command()
    {
        Setup(new AssetSuggestionStatus(true, true, 0.5,
            [new CaseSuggestionStatus(7, "В-3/26", CaseSuggestionState.NotChecked, 1, null)], []));
        _mediator.Send(Arg.Any<RequestPersonSuggestionCommand>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<ResponseDto<Guid>>(ResponseDto<Guid>.Ok(Guid.NewGuid())));

        var cut = RenderPanel();
        cut.Markup.ShouldContain("ещё не сверялось (эталонов у фигурантов: 1)");
        cut.FindAll("button").First(b => b.TextContent.Contains("Сверить сейчас", StringComparison.Ordinal)).Click();

        _mediator.Received(1).Send(Arg.Is<RequestPersonSuggestionCommand>(c => c.AssetId == AssetId), Arg.Any<CancellationToken>());
        cut.Markup.ShouldContain("Сверка поставлена в очередь");
    }

    [Fact(DisplayName = "Носитель ещё не обработан — «сверка пройдёт после обработки», кнопки нет")]
    public void Not_indexed_asset_waits()
    {
        Setup(new AssetSuggestionStatus(true, true, 0.5,
            [new CaseSuggestionStatus(7, "В-3/26", CaseSuggestionState.NotChecked, 1, null)], []));

        var cut = RenderPanel(MediaIndexStatus.Processing, faces: 0);

        cut.Markup.ShouldContain("Сверка пройдёт автоматически после обработки носителя.");
        cut.FindAll("button").Any(b => b.TextContent.Contains("Сверить сейчас", StringComparison.Ordinal)).ShouldBeFalse();
    }

    private void Setup(AssetSuggestionStatus status) =>
        _mediator.Send(Arg.Any<GetAssetSuggestionStatusQuery>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<ResponseDto<AssetSuggestionStatus>>(ResponseDto<AssetSuggestionStatus>.Ok(status)));

    private IRenderedComponent<PersonSuggestionPanel> RenderPanel(MediaIndexStatus status = MediaIndexStatus.Indexed, int faces = 1)
    {
        Render<MudPopoverProvider>();
        return Render<PersonSuggestionPanel>(p => p
            .Add(x => x.AssetId, AssetId)
            .Add(x => x.IndexStatus, status)
            .Add(x => x.FaceCount, faces));
    }

    private static SuggestionRunRow Run(int candidates) =>
        new(1, AssetId, 7, SuggestionTrigger.Indexing, 1, candidates > 0 ? 1 : 0, candidates, 0.5, RunAt);
}
