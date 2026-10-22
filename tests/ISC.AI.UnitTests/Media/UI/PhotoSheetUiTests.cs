using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Bunit;
using ISC.AI.Abstractions.Application;
using ISC.AI.Modules.Media.Application.Features.Assets;
using ISC.AI.Modules.Media.Application.Features.Print;
using ISC.AI.Modules.Media.Application.Features.Scope;
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
/// Печать фото (ТФ-МЕД-20, ADR-0057): страница — снимки из адреса (недоступные не показываются, число — предупреждением),
/// раскладка по умолчанию по числу снимков, предпросмотр той же раскладкой, что PDF, выгрузка — один запрос с подписями
/// и цветами; «Назад» — только путь системы; в медиатеке дела фото отмечаются галочками и уходят на печать.
/// </summary>
public sealed class PhotoSheetUiTests : BunitContext, IAsyncLifetime
{
    private readonly IMediator _mediator = Substitute.For<IMediator>();
    private readonly List<ExportPhotoSheetQuery> _exports = [];

    public PhotoSheetUiTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices();
        Services.AddSingleton(_mediator);
        Services.AddSingleton<ISC.AI.Modules.Media.Domain.Services.IMediaLinks>(new ISC.AI.Modules.Media.Application.Features.Navigation.NoMediaLinks());
        Services.AddSingleton(ISC.AI.Modules.Media.Application.MediaUploadOptions.Default);
        Services.AddSingleton(new ISC.AI.Modules.Media.Application.MediaSearchOptions());
        SetRendererInfo(new RendererInfo("Server", true));

        IReadOnlyList<PrintableAsset> rows =
        [
            new(5, "рынок.jpg", "0123456789abcdef0123456789abcdef.jpg", null, 1, 2, "фото"),
            new(6, "кадр.png", "fedcba9876543210fedcba9876543210.png", null, 3, 0, "снимок кадра"),
        ];
        _mediator.Send(Arg.Any<ListPrintableAssetsQuery>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<ResponseDto<IReadOnlyList<PrintableAsset>>>(ResponseDto<IReadOnlyList<PrintableAsset>>.Ok(rows)));
        _mediator.Send(Arg.Do<ExportPhotoSheetQuery>(q => _exports.Add(q)), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<ResponseDto<PhotoSheetFile>>(
                ResponseDto<PhotoSheetFile>.Ok(new PhotoSheetFile("Фототаблица.pdf", [37, 80, 68, 70], 3, 1))));
    }

    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;

    Task IAsyncLifetime.DisposeAsync() => base.DisposeAsync().AsTask();

    [Fact(DisplayName = "Страница: снимки из адреса, недоступные — числом; два снимка — по 2 на лист, один лист предпросмотра; гриф листа — максимум")]
    public void Page_lists_photos_and_previews()
    {
        var cut = RenderPrint("/media/print?assets=5,6,99&back=%2Fmedia%2Fcases%2F3");

        cut.WaitForAssertion(() => cut.Markup.ShouldContain("рынок.jpg"));
        _mediator.Received(1).Send(Arg.Is<ListPrintableAssetsQuery>(q => q.AssetIds.SequenceEqual(new[] { 5, 6, 99 })), Arg.Any<CancellationToken>());
        cut.Markup.ShouldContain("Не показано снимков: 1");
        cut.Markup.ShouldContain("снимков: 2 · листов: 1");
        cut.Markup.ShouldContain("лист 1 из 1");
        cut.Markup.ShouldContain("СЕКРЕТНО"); // гриф листа — максимум по снимкам (3)
        cut.Markup.ShouldContain("с пометками (2)"); // пометки только у первого
        cut.Find("a[href='/media/cases/3']").TextContent.ShouldContain("Назад");
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "2").ClassList.ShouldContain("mud-button-filled");

        // Один на лист — два листа и выбор размера.
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "1").Click();
        cut.Markup.ShouldContain("лист 2 из 2");
        cut.Markup.ShouldContain("Размер снимка");
    }

    [Fact(DisplayName = "«Скачать PDF»: один запрос — снимки по порядку с подписями, цвета, пометки, раскладка; файл уходит браузеру")]
    public void Download_sends_one_export()
    {
        var cut = RenderPrint("/media/print?assets=5,6");
        cut.WaitForAssertion(() => cut.Markup.ShouldContain("рынок.jpg"));

        cut.FindAll("input").First(i => i.GetAttribute("placeholder") == "Подпись: псевдоним, дата, место").Input("Шторм");
        cut.FindAll("button").Single(b => b.TextContent.Contains("Скачать PDF", StringComparison.Ordinal)).Click();

        cut.WaitForAssertion(() => _exports.Count.ShouldBe(1));
        var export = _exports[0];
        export.PerPage.ShouldBe(2);
        export.SingleWidthMm.ShouldBeNull();
        export.Entries.Select(e => (e.AssetId, e.Caption, e.TextColor, e.BackgroundColor, e.WithAnnotations))
            .ShouldBe([(5, "Шторм", "#000000", "#FFFFFF", true), (6, (string?)null, "#000000", "#FFFFFF", false)]);
        cut.WaitForAssertion(() => JSInterop.Invocations["downloadFileFromStream"].Count.ShouldBe(1));
        JSInterop.Invocations["downloadFileFromStream"][0].Arguments[0].ShouldBe("Фототаблица.pdf");
    }

    [Fact(DisplayName = "«Назад» — только путь системы: внешний адрес из параметра не показывается")]
    public void Back_link_is_internal_only()
    {
        var external = RenderPrint("/media/print?assets=5&back=%2F%2Fevil.example");

        external.WaitForAssertion(() => external.Markup.ShouldContain("рынок.jpg"));
        external.FindAll("a").ShouldNotContain(a => (a.GetAttribute("href") ?? string.Empty).Contains("evil", StringComparison.Ordinal));
    }

    [Fact(DisplayName = "Без снимков в адресе — подсказка, как выбрать, без утверждения «нет таких»; сервер не спрашивается")]
    public void Empty_address_explains()
    {
        var empty = RenderPrint("/media/print");

        empty.WaitForAssertion(() => empty.Markup.ShouldContain("Печатаются только фото ваших дел"));
        _mediator.DidNotReceive().Send(Arg.Any<ListPrintableAssetsQuery>(), Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "Медиатека дела: галочки только у фото; «Печать фото» ведёт на страницу печати с отмеченными и обратным адресом")]
    public void Case_media_selects_photos_for_print()
    {
        Render<MudPopoverProvider>();
        _mediator.Send(Arg.Any<ListAccessibleCasesQuery>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<ResponseDto<IReadOnlyList<CaseScopeItem>>>(
                ResponseDto<IReadOnlyList<CaseScopeItem>>.Ok([new CaseScopeItem(3, "12-345", "Дело", 0, 1)])));
        _mediator.Send(Arg.Any<ListCaseMediaQuery>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<ResponseDto<IReadOnlyList<MediaAssetRow>>>(ResponseDto<IReadOnlyList<MediaAssetRow>>.Ok(
            [
                Asset(5, MediaKind.Image, "рынок.jpg"),
                Asset(6, MediaKind.Video, "запись.mp4"),
                Asset(7, MediaKind.Image, "кадр.png"),
            ])));

        var cut = Render<CaseMedia>(p => p.Add(x => x.CaseId, 3));
        cut.WaitForAssertion(() => cut.Markup.ShouldContain("рынок.jpg"));

        cut.FindAll("td[data-label='Печать'] input[type=checkbox]").Count.ShouldBe(2); // у видео галочки нет
        var print = cut.FindAll("button").Single(b => b.TextContent.Contains("Печать фото", StringComparison.Ordinal));
        print.HasAttribute("disabled").ShouldBeTrue();

        cut.FindAll("td[data-label='Печать'] input[type=checkbox]")[1].Change(true);
        cut.FindAll("button").Single(b => b.TextContent.Contains("Печать фото", StringComparison.Ordinal)).Click();

        Services.GetRequiredService<NavigationManager>().Uri.ShouldEndWith("/media/print?assets=7&back=%2Fmedia%2Fcases%2F3");
    }

    private IRenderedComponent<PhotoSheetPrint> RenderPrint(string address)
    {
        Render<MudPopoverProvider>();
        Services.GetRequiredService<NavigationManager>().NavigateTo(address);
        return Render<PhotoSheetPrint>();
    }

    private static MediaAssetRow Asset(int id, MediaKind kind, string fileName) => new(
        id, kind, fileName, "0123456789abcdef0123456789abcdef.bin", kind == MediaKind.Image ? "image/jpeg" : "video/mp4", 1024, null, null, null,
        0, 1, 1, MediaIndexStatus.Indexed, null, null, null, null, DateTime.UtcNow, 0);
}
