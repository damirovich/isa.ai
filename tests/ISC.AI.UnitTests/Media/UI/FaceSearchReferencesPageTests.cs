using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Bunit;
using ISC.AI.Abstractions.Application;
using ISC.AI.Modules.Media.Application.Features.Scope;
using ISC.AI.Modules.Media.Application.Features.Search;
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

// Подмены NSubstitute для IMediator.Send: ValueTask здесь не исполняется, а задаёт ответ или проверяет вызов
// (CA2012 к этому неприменим).
#pragma warning disable CA2012

namespace ISC.AI.UnitTests.Media.UI;

/// <summary>
/// Страница поиска по лицу с эталонами фигуранта из адреса (ТФ-ПЕР-03, ADR-0046): все эталоны видны, непригодный
/// показан, но в поиск не идёт; поиск — одним запросом по списку эталонов; в выдаче — сколько эталонов искали,
/// пропущенные и у кандидата «эталон № … · совпало эталонов: k из N».
/// </summary>
public sealed class FaceSearchReferencesPageTests : BunitContext, IAsyncLifetime
{
    private static readonly int[] UsableReferences = [5, 7];
    private readonly IMediator _mediator = Substitute.For<IMediator>();

    public FaceSearchReferencesPageTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices();
        Services.AddSingleton(_mediator);
        Services.AddSingleton(new ISC.AI.Modules.Media.Application.MediaSearchOptions());
        SetRendererInfo(new RendererInfo("Server", true));

        _mediator.Send(Arg.Any<ListAccessibleCasesQuery>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<ResponseDto<IReadOnlyList<CaseScopeItem>>>(ResponseDto<IReadOnlyList<CaseScopeItem>>.Ok(
                [new CaseScopeItem(3, "№ 1", "Дело", Classification: 2, DivisionId: 1)])));
        _mediator.Send(Arg.Any<ListCaseAuthorizationsQuery>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<ResponseDto<IReadOnlyList<CaseAuthorizationItem>>>(ResponseDto<IReadOnlyList<CaseAuthorizationItem>>.Ok(
                [new CaseAuthorizationItem(9, "Постановление № 5")])));
        Face(5, acceptable: true);
        Face(6, acceptable: false);
        Face(7, acceptable: true);

        _mediator.Send(Arg.Any<SearchByFaceQuery>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<ResponseDto<FaceSearchResult>>(ResponseDto<FaceSearchResult>.Ok(new FaceSearchResult(
                42, "abcdef0123456789", null, 0,
                [
                    new SearchCandidateRow(1, 42, 3, 1, 100, 60, null, null, 0.1, "c.jpg", "sface-1", 2, 1, CandidateStatus.Candidate, null, [],
                        ProbeFaceId: 7, ProbeMatchCount: 2),
                ],
                "yunet-1", "sface-1", 20, null, [3], ProbeFaceIds: [5, 7], SkippedProbes: ["эталон — лицо № 9: шаблон удалён"]))));
    }

    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;

    Task IAsyncLifetime.DisposeAsync() => base.DisposeAsync().AsTask();

    [Fact(DisplayName = "ADR-0046: эталоны из адреса — поиск одним запросом по пригодным; в выдаче — эталоны, пропущенные и согласие эталонов")]
    public void References_from_address_search_by_all()
    {
        // Как переход «Искать по материалам» из карточки фигуранта: параметры — в адресе страницы.
        Services.GetRequiredService<NavigationManager>().NavigateTo("/media/search?caseId=3&faceIds=5,6,7&authId=9&scope=all&auto=1");
        Render<MudPopoverProvider>();
        var cut = Render<FaceSearch>();

        cut.WaitForAssertion(() => _mediator.Received(1).Send(
            Arg.Is<SearchByFaceQuery>(q => q.ProbeFaceIds != null && q.ProbeFaceIds.SequenceEqual(UsableReferences)
                && q.ProbeFaceId == null && q.ProbeImage == null && q.Scope == SearchScopeKind.AllAccessibleCases && q.AuthorizationId == 9),
            Arg.Any<CancellationToken>()));

        cut.Markup.ShouldContain("Поиск по всем эталонам фигуранта: 2 из 3");
        cut.Markup.ShouldContain("Искали по 2 эталонам фигуранта");
        cut.Markup.ShouldContain("Пропущены эталоны: эталон — лицо № 9: шаблон удалён");
        cut.Markup.ShouldContain("эталон № 7 · совпало эталонов: 2 из 2");
    }

    [Fact(DisplayName = "ТФ-ПЛ-06: порог задаётся схожестью в процентах — «60» уходит в поиск расстоянием 0,4; в выдаче — «схожесть от … %»")]
    public void Similarity_percent_becomes_distance()
    {
        Services.GetRequiredService<NavigationManager>().NavigateTo("/media/search?caseId=3&faceIds=5,7&authId=9&scope=all");
        Render<MudPopoverProvider>();
        var cut = Render<FaceSearch>();
        cut.WaitForAssertion(() => cut.Markup.ShouldContain("Схожесть не ниже, %"));
        cut.Markup.ShouldContain("не ниже 20 % — предел эксплуатанта");
        cut.Markup.ShouldNotContain("Порог расстояния");

        var field = cut.FindAll(".mud-input-control")
            .Single(e => e.TextContent.Contains("Схожесть не ниже, %", StringComparison.Ordinal))
            .QuerySelector("input")!;
        field.Change("60");
        cut.FindAll("button").Single(b => b.TextContent.Contains("Искать", StringComparison.Ordinal)).Click();

        cut.WaitForAssertion(() => _mediator.Received(1).Send(
            Arg.Is<SearchByFaceQuery>(q => q.MaxCosineDistance.HasValue && Math.Abs(q.MaxCosineDistance.Value - 0.4) < 1e-9),
            Arg.Any<CancellationToken>()));
        cut.Markup.ShouldContain("без порога"); // ответ подменён без порога — подпись выдачи берёт его из результата
    }

    [Fact(DisplayName = "ТФ-ПЛ-06: перевод «схожесть, %» ↔ «расстояние» в обе стороны; пусто — без порога")]
    public void Similarity_conversions()
    {
        MediaLabels.MaxCosineDistance(60).ShouldBe(0.4);
        MediaLabels.MaxCosineDistance(100).ShouldBe(0);
        MediaLabels.MaxCosineDistance(null).ShouldBeNull();
        MediaLabels.MinSimilarityPercent(0.7).ShouldBe(30);
        MediaLabels.MinSimilarityPercent(0.5).ShouldBe(50);
        MediaLabels.SimilarityThreshold(0.5).ShouldBe("схожесть от 50 %");
        MediaLabels.SimilarityThreshold(null).ShouldBe("без порога");
    }

    private void Face(int id, bool acceptable) =>
        _mediator.Send(Arg.Is<GetFaceQuery>(q => q.FaceId == id), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<ResponseDto<FaceRow>>(ResponseDto<FaceRow>.Ok(
                new FaceRow(id, 50 + id, null, null, 0, 0, 10, 10, 0.9f, acceptable ? 0.8f : 0.1f, acceptable, acceptable ? null : "размыто", "c.jpg", null, 2, 1))));
}
