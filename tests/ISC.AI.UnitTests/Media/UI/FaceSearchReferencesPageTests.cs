using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Bunit;
using ISC.AI.Abstractions.Application;
using ISC.AI.Modules.Media.Application.Features.Scope;
using ISC.AI.Modules.Media.Application.Features.Search;
using ISC.AI.Modules.Media.Application.Features.Settings;
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

    [Fact(DisplayName = "ТФ-ПЛ-08: ползунок порога — от нижней границы, открыт на рабочем пороге; предустановка уходит в поиск процентом")]
    public void Threshold_slider_starts_at_working_threshold_and_sends_percent()
    {
        GivenSettings(threshold: 80, min: 72);
        Services.GetRequiredService<NavigationManager>().NavigateTo("/media/search?caseId=3&faceIds=5,7&authId=9&scope=all");
        Render<MudPopoverProvider>();
        var cut = Render<FaceSearch>();
        cut.WaitForAssertion(() => ThresholdValue(cut).ShouldBe("80 %"));

        var slider = cut.Find("input[type=range]");
        slider.GetAttribute("min").ShouldBe("72");
        slider.GetAttribute("max").ShouldBe("99");
        cut.FindAll(".mud-toggle-item").Select(t => t.TextContent.Trim()).ShouldBe(["Низкий · 72 %", "Средний · 80 %", "Высокий · 90 %"]);
        cut.Markup.ShouldContain("Кандидаты ниже порога не показываются");
        cut.Markup.ShouldContain("72 % · нижняя граница");
        cut.Markup.ShouldContain("Кандидатов в выдаче");

        cut.FindAll(".mud-toggle-item").Single(t => t.TextContent.Contains("Высокий", StringComparison.Ordinal)).Click();
        cut.WaitForAssertion(() => ThresholdValue(cut).ShouldBe("90 %"));
        cut.FindAll("button").Single(b => b.TextContent.Contains("Искать", StringComparison.Ordinal)).Click();

        cut.WaitForAssertion(() => _mediator.Received(1).Send(
            Arg.Is<SearchByFaceQuery>(q => q.MinSimilarityPercent == 90), Arg.Any<CancellationToken>()));
        cut.Markup.ShouldContain("без порога"); // ответ подменён без порога — подпись выдачи берёт его из результата
    }

    [Fact(DisplayName = "ТФ-ПЛ-08: ниже нижней границы страница порог не отправляет — даже если ползунок сдвинули мимо его пределов")]
    public void Threshold_below_bound_is_not_sent()
    {
        GivenSettings(threshold: 85, min: 70);
        Services.GetRequiredService<NavigationManager>().NavigateTo("/media/search?caseId=3&faceIds=5,7&authId=9&scope=all");
        Render<MudPopoverProvider>();
        var cut = Render<FaceSearch>();
        cut.WaitForAssertion(() => ThresholdValue(cut).ShouldBe("85 %"));

        cut.Find("input[type=range]").Input("50");
        cut.FindAll("button").Single(b => b.TextContent.Contains("Искать", StringComparison.Ordinal)).Click();

        cut.WaitForAssertion(() => _mediator.Received(1).Send(
            Arg.Is<SearchByFaceQuery>(q => q.MinSimilarityPercent == 70), Arg.Any<CancellationToken>()));
    }

    [Fact(DisplayName = "ТФ-ПЛ-06: совпавшие предустановки показываются один раз — граница равна рабочему порогу")]
    public void Equal_presets_are_shown_once()
    {
        GivenSettings(threshold: 85, min: 85);
        Services.GetRequiredService<NavigationManager>().NavigateTo("/media/search?caseId=3&faceIds=5,7&authId=9&scope=all");
        Render<MudPopoverProvider>();
        var cut = Render<FaceSearch>();
        cut.WaitForAssertion(() => ThresholdValue(cut).ShouldBe("85 %"));

        cut.FindAll(".mud-toggle-item").Select(t => t.TextContent.Trim()).ShouldBe(["Низкий · 85 %", "Высокий · 92 %"]);
    }

    [Fact(DisplayName = "Форма поиска: пустая проба — область выбора с кнопкой; без дела — подсказка вместо сводки, «Искать» недоступна")]
    public void Empty_form_shows_probe_zone_and_hint()
    {
        GivenSettings(threshold: 85, min: 70);
        Services.GetRequiredService<NavigationManager>().NavigateTo("/media/search");
        Render<MudPopoverProvider>();
        var cut = Render<FaceSearch>();
        cut.WaitForAssertion(() => ThresholdValue(cut).ShouldBe("85 %"));

        cut.Markup.ShouldContain("Выберите изображение с лицом");
        cut.Markup.ShouldContain("«Найти в других материалах»");
        cut.FindAll("button").ShouldContain(b => b.TextContent.Contains("Выбрать изображение", StringComparison.Ordinal));
        cut.Markup.ShouldContain("Выберите дело.");
        cut.Markup.ShouldNotContain("до 20 кандидатов");
        cut.FindAll("button").Single(b => b.TextContent.Contains("Искать", StringComparison.Ordinal)).HasAttribute("disabled").ShouldBeTrue();
        cut.Markup.ShouldContain("Только материалы выбранного дела.");
    }

    [Fact(DisplayName = "Форма поиска: всё задано — сводка области, порога и длины выдачи рядом с «Искать»; пояснение — к выбранной области")]
    public void Ready_form_shows_summary()
    {
        GivenSettings(threshold: 85, min: 70);
        Services.GetRequiredService<NavigationManager>().NavigateTo("/media/search?caseId=3&faceIds=5,7&authId=9&scope=all");
        Render<MudPopoverProvider>();
        var cut = Render<FaceSearch>();
        cut.WaitForAssertion(() => cut.Markup.ShouldContain("все дела в пределах допуска"));

        cut.Markup.ShouldContain("схожесть от 85 %");
        cut.Markup.ShouldContain("до 20 кандидатов");
        cut.Markup.ShouldContain("включая дела других сотрудников");
        cut.FindAll("button").Single(b => b.TextContent.Contains("Искать", StringComparison.Ordinal)).HasAttribute("disabled").ShouldBeFalse();
    }

    [Fact(DisplayName = "Проба-изображение: имя файла показано один раз — собственный список файлов MudFileUpload скрыт; есть «Заменить…» и «Убрать»")]
    public void Selected_probe_file_name_is_shown_once()
    {
        GivenSettings(threshold: 85, min: 70);
        Services.GetRequiredService<NavigationManager>().NavigateTo("/media/search");
        Render<MudPopoverProvider>();
        var cut = Render<FaceSearch>();
        cut.WaitForAssertion(() => ThresholdValue(cut).ShouldBe("85 %"));

        cut.FindComponent<Microsoft.AspNetCore.Components.Forms.InputFile>()
            .UploadFiles(InputFileContent.CreateFromBinary([1, 2, 3, 4], "probe-photo.jpg", contentType: "image/jpeg"));

        cut.WaitForAssertion(() => cut.Markup.ShouldContain("probe-photo.jpg"));
        cut.Markup.Split(">probe-photo.jpg<").Length.ShouldBe(2); // имя — одним текстом в карточке пробы
        cut.FindAll(".mud-chip").ShouldNotContain(c => c.TextContent.Contains("probe-photo.jpg", StringComparison.Ordinal));
        cut.FindAll("button").ShouldContain(b => b.TextContent.Contains("Заменить", StringComparison.Ordinal));
        cut.FindAll("button").ShouldContain(b => b.TextContent.Contains("Убрать", StringComparison.Ordinal));
    }

    [Fact(DisplayName = "ADR-0054: подпись порога выдачи — процентом шкалы; сессия без порога — «без порога»")]
    public void Similarity_threshold_label_uses_scale()
    {
        MediaLabels.SimilarityThreshold(SimilarityScale.Default, SimilarityScale.Default.MaxCosineDistance(85)).ShouldBe("схожесть от 85 %");
        MediaLabels.SimilarityThreshold(SimilarityScale.Default, 0.5).ShouldBe("схожесть от 76 %"); // прежний порог предложений
        MediaLabels.SimilarityThreshold(SimilarityScale.Default, null).ShouldBe("без порога");
        MediaLabels.HighThresholdPreset(85).ShouldBe(92);
        MediaLabels.HighThresholdPreset(99).ShouldBe(99);
    }

    // Крупное значение порога над ползунком (объявляется вспомогательным технологиям при изменении).
    private static string ThresholdValue(IRenderedComponent<FaceSearch> cut) => cut.Find("[aria-live=polite]").TextContent.Trim();

    private void GivenSettings(int threshold, int min) =>
        _mediator.Send(Arg.Any<GetMediaSettingsQuery>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<ResponseDto<MediaSettingsView>>(ResponseDto<MediaSettingsView>.Ok(new MediaSettingsView(
                ConfirmationMode.SingleOperator, threshold, min, null, null, CanManage: false, "балл 0,363 = 70 %"))));

    private void Face(int id, bool acceptable) =>
        _mediator.Send(Arg.Is<GetFaceQuery>(q => q.FaceId == id), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<ResponseDto<FaceRow>>(ResponseDto<FaceRow>.Ok(
                new FaceRow(id, 50 + id, null, null, 0, 0, 10, 10, 0.9f, acceptable ? 0.8f : 0.1f, acceptable, acceptable ? null : "размыто", "c.jpg", null, 2, 1))));
}
