using Xunit;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Bunit;
using ISC.AI.Abstractions.Application;
using ISC.AI.Modules.Media.Application.Features.Annotations;
using ISC.AI.Modules.Media.Application.Features.Assets;
using ISC.AI.Modules.Media.Application.Features.Assets.Commands.EditPhoto;
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

namespace ISC.AI.UnitTests.Media.UI;

/// <summary>
/// Обработка фото и пометки в интерфейсе (ТФ-МЕД-13/14, ADR-0050): страница обработки (запреты, предпросмотр через
/// media.js, «зум лица», сохранение копии), слой пометок (только просмотр — без инструментов, рисование и подпись,
/// снятие, выгрузка с пометками) и карточка носителя («Обработать», пометка у копии).
/// </summary>
public sealed class PhotoEditingUiTests : BunitContext, IAsyncLifetime
{
    private const string ModulePath = "./_content/ISC.AI.Modules.Media.UI/js/media.js";
    private readonly IMediator _mediator = Substitute.For<IMediator>();
    private readonly BunitJSModuleInterop _module;

    public PhotoEditingUiTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        _module = JSInterop.SetupModule(ModulePath);
        _module.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices();
        Services.AddSingleton(_mediator);
        Services.AddSingleton<ISC.AI.Modules.Media.Domain.Services.IMediaLinks>(new ISC.AI.Modules.Media.Application.Features.Navigation.NoMediaLinks());
        Services.AddSingleton(ISC.AI.Modules.Media.Application.MediaUploadOptions.Default);
        Services.AddSingleton(new ISC.AI.Modules.Media.Application.MediaSearchOptions());
        SetRendererInfo(new RendererInfo("Server", true));
        _mediator.Send(Arg.Any<GetAssetSuggestionStatusQuery>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<ResponseDto<AssetSuggestionStatus>>(ResponseDto<AssetSuggestionStatus>.Ok(
                new AssetSuggestionStatus(true, false, 0.5, [], []))));
        Layer();
    }

    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;

    Task IAsyncLifetime.DisposeAsync() => base.DisposeAsync().AsTask();

    // ---------------- страница обработки ----------------

    [Theory(DisplayName = "Обработка: не фото, сама копия и материал чужого дела — причина вместо редактора, предпросмотр не грузится")]
    [InlineData(MediaKind.Video, false, false, CreateEditedCopyCommand.NotImageMessage)]
    [InlineData(MediaKind.Image, true, false, CreateEditedCopyCommand.AlreadyCopyMessage)]
    [InlineData(MediaKind.Image, false, true, "открыт только на просмотр")]
    public void Editor_blocked_states(MediaKind kind, bool copy, bool viewOnly, string message)
    {
        Details(Photo(kind, copy ? new ImageEdit(Brightness: 10).ToJson() : null), viewOnly: viewOnly);

        var cut = Render<ImageEditor>(p => p.Add(x => x.Id, 5));

        cut.WaitForAssertion(() => cut.Markup.ShouldContain(message));
        cut.FindAll("canvas").ShouldBeEmpty();
        _module.Invocations["initImageEditor"].ShouldBeEmpty();
    }

    [Fact(DisplayName = "Обработка: предпросмотр из оригинала; «Сохранить» доступна после загрузки и правки; поворот — в перечне, размер результата, команда и переход к копии")]
    public void Editor_rotates_and_saves_copy()
    {
        Details(Photo(MediaKind.Image));
        CreateEditedCopyCommand? sent = null;
        _mediator.Send(Arg.Do<CreateEditedCopyCommand>(c => sent = c), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<ResponseDto<EditedCopyResult>>(ResponseDto<EditedCopyResult>.Ok(
                new EditedCopyResult(77, false, "поворот 90°"))));
        Render<MudPopoverProvider>();

        var cut = Render<ImageEditor>(p => p.Add(x => x.Id, 5));

        cut.WaitForAssertion(() => _module.Invocations["initImageEditor"].Count.ShouldBe(1));
        var init = _module.Invocations["initImageEditor"][0];
        init.Arguments[0].ShouldBe("media-image-editor-5");
        init.Arguments[1]!.ToString()!.ShouldBe("/media/files/media-originals/5/0123456789abcdef0123456789abcdef.jpg");
        SaveButton(cut).HasAttribute("disabled").ShouldBeTrue("набор пуст и размер ещё не известен");

        cut.InvokeAsync(() => cut.Instance.OnImageLoaded(400, 300));
        cut.FindAll("button").First(b => b.TextContent.Contains("Вправо")).Click();

        cut.Markup.ShouldContain("поворот 90°");
        cut.Markup.ShouldContain("Результат: 300 × 400 пикс.");
        _module.Invocations["updateImageEditor"].ShouldNotBeEmpty();
        SaveButton(cut).HasAttribute("disabled").ShouldBeFalse();
        SaveButton(cut).Click();

        sent.ShouldNotBeNull().AssetId.ShouldBe(5);
        sent.Edit.ShouldBe(new ImageEdit(Rotation: 90));
        Services.GetRequiredService<NavigationManager>().Uri.ShouldEndWith("/media/assets/77");
    }

    [Fact(DisplayName = "Обработка: «Лицо № N» кадрирует по рамке с полями и увеличивает; отказ сервера показан, переход не выполняется")]
    public void Editor_face_zoom_and_refusal()
    {
        Details(Photo(MediaKind.Image), faces: [new FaceRow(11, 5, null, null, 100, 100, 80, 80, 0.9f, 0.8f, true, null, null, null, 0, 1)]);
        _mediator.Send(Arg.Any<CreateEditedCopyCommand>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<ResponseDto<EditedCopyResult>>(ResponseDto<EditedCopyResult>.Fail(CreateEditedCopyCommand.NoRightMessage)));
        Render<MudPopoverProvider>();
        var cut = Render<ImageEditor>(p => p.Add(x => x.Id, 5));
        cut.WaitForAssertion(() => _module.Invocations["initImageEditor"].ShouldNotBeEmpty());
        cut.InvokeAsync(() => cut.Instance.OnImageLoaded(1000, 800));

        cut.FindAll(".mud-chip").First(c => c.TextContent.Contains("Лицо № 11")).Click();

        cut.Markup.ShouldContain("кадрирование 120×120 от (80, 80); увеличение ×4");
        cut.Markup.ShouldContain("Результат: 480 × 480 пикс.");
        var before = Services.GetRequiredService<NavigationManager>().Uri;
        SaveButton(cut).Click();
        cut.Markup.ShouldContain(CreateEditedCopyCommand.NoRightMessage);
        Services.GetRequiredService<NavigationManager>().Uri.ShouldBe(before);
    }

    // ---------------- слой пометок ----------------

    [Fact(DisplayName = "Пометки «только просмотр»: видны слой и список с автором, инструментов, снятия и выгрузки нет")]
    public void Annotations_view_only()
    {
        Layer(Rect(1, 42), Text(2, "Вход", null));

        var cut = RenderAnnotations(canEdit: false);

        cut.WaitForAssertion(() => cut.FindAll(".pa-item").Count.ShouldBe(2));
        cut.Find("svg.pa-layer rect").GetAttribute("stroke").ShouldBe(AnnotationColors.Red);
        cut.Find("svg.pa-layer text").TextContent.ShouldBe("Вход");
        cut.Markup.ShouldContain("Показывать пометки (2)");
        cut.Markup.ShouldContain("Иванов И.И.");
        cut.Markup.ShouldContain("автор не указан");
        cut.FindAll("button").Where(b => b.TextContent.Contains("Стрелка") || b.TextContent.Contains("Скачать с пометками")).ShouldBeEmpty();
        cut.FindAll("button[aria-label='Снять пометку']").ShouldBeEmpty();
        cut.FindAll("svg.pa-draw").ShouldBeEmpty();
        _module.Invocations["bindAnnotationLayer"].ShouldBeEmpty();
    }

    [Fact(DisplayName = "Пометки: пока размер фото не известен — слоёв нет, инструменты неактивны")]
    public void Annotations_wait_for_size()
    {
        Layer(Rect(1, 42));

        var cut = RenderAnnotations(canEdit: true, width: 0, height: 0);

        cut.WaitForAssertion(() => cut.FindAll(".pa-item").Count.ShouldBe(1));
        cut.FindAll("svg").Where(s => s.ClassList.Contains("pa-layer") || s.ClassList.Contains("pa-draw")).ShouldBeEmpty();
        cut.FindAll("button").First(b => b.TextContent.Contains("Стрелка")).HasAttribute("disabled").ShouldBeTrue();
    }

    [Fact(DisplayName = "Пометки: слой рисования привязан; нарисованная рамка уходит командой выбранным цветом, список перечитан")]
    public void Annotations_draw_rectangle()
    {
        AddAnnotationCommand? sent = null;
        _mediator.Send(Arg.Do<AddAnnotationCommand>(c => sent = c), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<ResponseDto<int>>(ResponseDto<int>.Ok(9)));
        var cut = RenderAnnotations(canEdit: true);
        cut.WaitForAssertion(() => _module.Invocations["bindAnnotationLayer"].Count.ShouldBe(1));
        _module.Invocations["bindAnnotationLayer"][0].Arguments[0].ShouldBe("media-annotation-draw-5");

        cut.FindAll("button").First(b => b.TextContent.Contains("Рамка")).Click();
        cut.Find("button[aria-label='Синий']").Click();
        _module.Invocations["setAnnotationTool"].Last().Arguments.ShouldBe(["media-annotation-draw-5", "rect", AnnotationColors.Blue]);
        cut.Markup.ShouldContain("Протяните мышью по диагонали рамки.");
        Layer(Rect(9, 42));
        cut.InvokeAsync(() => cut.Instance.OnAnnotationDrawn("rect", 10, 20, 110, 220));

        sent.ShouldNotBeNull().Draft.ShouldBe(new AnnotationDraft(5, AnnotationKind.Rectangle, 10, 20, 110, 220, null, AnnotationColors.Blue));
        cut.WaitForAssertion(() => cut.FindAll(".pa-item").Count.ShouldBe(1));
    }

    [Fact(DisplayName = "Пометки: подпись — щелчок спрашивает текст в окне, «Поставить» отправляет его в точку щелчка")]
    public void Annotations_text_dialog()
    {
        AddAnnotationCommand? sent = null;
        _mediator.Send(Arg.Do<AddAnnotationCommand>(c => sent = c), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<ResponseDto<int>>(ResponseDto<int>.Ok(9)));
        Render<MudPopoverProvider>();
        var dialogs = Render<MudDialogProvider>();
        var cut = RenderAnnotations(canEdit: true);

        cut.InvokeAsync(() => cut.Instance.OnAnnotationDrawn("text", 50, 60, 50, 60));

        dialogs.WaitForAssertion(() => dialogs.Markup.ShouldContain("Подпись на фото"));
        dialogs.Find("input").Input("Вход во двор");
        dialogs.FindAll("button").First(b => b.TextContent.Contains("Поставить")).Click();
        sent.ShouldNotBeNull().Draft.ShouldBe(new AnnotationDraft(5, AnnotationKind.Text, 50, 60, 50, 60, "Вход во двор", AnnotationColors.Red));
    }

    [Fact(DisplayName = "Пометки: снятие — команда с номером пометки; отказ сервера показан предупреждением")]
    public void Annotations_delete_and_refusal()
    {
        Layer(Rect(4, 42));
        DeleteAnnotationCommand? sent = null;
        _mediator.Send(Arg.Do<DeleteAnnotationCommand>(c => sent = c), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<ResponseDto<bool>>(ResponseDto<bool>.Fail(AnnotationMessages.NotFound)));
        var cut = RenderAnnotations(canEdit: true);
        cut.WaitForAssertion(() => cut.FindAll("button[aria-label='Снять пометку']").Count.ShouldBe(1));

        cut.Find("button[aria-label='Снять пометку']").Click();

        sent.ShouldNotBeNull().AnnotationId.ShouldBe(4);
        cut.WaitForAssertion(() => cut.Markup.ShouldContain(AnnotationMessages.NotFound));
    }

    [Fact(DisplayName = "Пометки: «Скачать с пометками» — запрос выгрузки и файл в браузер под именем от сервера")]
    public void Annotations_export_downloads_file()
    {
        Layer(Rect(4, 42));
        _mediator.Send(Arg.Any<ExportAnnotatedImageQuery>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<ResponseDto<AnnotatedImage>>(ResponseDto<AnnotatedImage>.Ok(
                new AnnotatedImage("снимок (с пометками).png", [1, 2, 3]))));
        var cut = RenderAnnotations(canEdit: true);
        cut.WaitForAssertion(() => cut.Markup.ShouldContain("Скачать с пометками"));

        cut.FindAll("button").First(b => b.TextContent.Contains("Скачать с пометками")).Click();

        cut.WaitForAssertion(() => JSInterop.Invocations["downloadFileFromStream"].Count.ShouldBe(1));
        JSInterop.Invocations["downloadFileFromStream"][0].Arguments[0].ShouldBe("снимок (с пометками).png");
        _mediator.Received(1).Send(Arg.Is<ExportAnnotatedImageQuery>(q => q.AssetId == 5), Arg.Any<CancellationToken>());
    }

    // ---------------- карточка ----------------

    [Fact(DisplayName = "Карточка фото своего дела: «Обработать» ведёт на страницу обработки; фото — внутри слоя пометок")]
    public void Card_offers_editing_for_original()
    {
        var cut = RenderCard(Photo(MediaKind.Image));

        cut.WaitForAssertion(() => cut.FindComponents<PhotoAnnotations>().Count.ShouldBe(1));
        cut.FindAll("a").Single(a => a.TextContent.Contains("Обработать")).GetAttribute("href").ShouldBe("/media/assets/5/edit");
        cut.Find(".pa-frame img").GetAttribute("alt").ShouldBe("снимок.jpg");
    }

    [Fact(DisplayName = "Карточка копии: ссылка на источник и перечень операций, подпись под фото — «обработанная копия», «Обработать» нет")]
    public void Card_of_copy()
    {
        var copy = RenderCard(Photo(MediaKind.Image, new ImageEdit(Brightness: 20, Zoom: 2, Crop: new CropRect(0, 0, 10, 10)).ToJson(), sourceAssetId: 4));
        copy.WaitForAssertion(() => copy.Markup.ShouldContain("Обработанная копия носителя"));
        copy.Find(".edited-copy-note a").GetAttribute("href").ShouldBe("/media/assets/4");
        copy.Find(".edited-copy-note").TextContent.ShouldContain("кадрирование 10×10 от (0, 0); увеличение ×2; яркость +20");
        copy.Markup.ShouldContain("Показана обработанная копия");
        copy.FindAll("a").Where(a => a.TextContent.Contains("Обработать")).ShouldBeEmpty();
    }

    [Fact(DisplayName = "Карточка фото чужого дела (только просмотр): «Обработать» нет, пометки — без инструментов")]
    public void Card_view_only()
    {
        var viewOnly = RenderCard(Photo(MediaKind.Image), viewOnly: true);
        viewOnly.WaitForAssertion(() => viewOnly.FindComponents<PhotoAnnotations>().Count.ShouldBe(1));
        viewOnly.FindAll("a").Where(a => a.TextContent.Contains("Обработать")).ShouldBeEmpty();
        viewOnly.FindComponent<PhotoAnnotations>().Instance.CanEdit.ShouldBeFalse();
    }

    // ---------------- обвязка ----------------

    private static MediaAssetRow Photo(MediaKind kind, string? editOperations = null, int? sourceAssetId = null) => new(
        5, kind, "снимок.jpg", "0123456789abcdef0123456789abcdef.jpg", kind == MediaKind.Image ? "image/jpeg" : "video/mp4", 1024,
        null, null, null, 0, 1, 1, editOperations is null ? MediaIndexStatus.Indexed : MediaIndexStatus.NotApplicable,
        null, null, null, null, DateTime.UtcNow, 0, SourceAssetId: sourceAssetId, EditOperations: editOperations);

    private void Details(MediaAssetRow asset, IReadOnlyList<FaceRow>? faces = null, bool viewOnly = false) =>
        _mediator.Send(Arg.Any<GetMediaAssetQuery>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<ResponseDto<MediaAssetDetails>>(ResponseDto<MediaAssetDetails>.Ok(
                new MediaAssetDetails(asset, faces ?? [], 3, ViewOnly: viewOnly))));

    private void Layer(params AnnotationRow[] items) =>
        _mediator.Send(Arg.Any<ListAnnotationsQuery>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<ResponseDto<AnnotationLayer>>(ResponseDto<AnnotationLayer>.Ok(
                new AnnotationLayer(items, new Dictionary<int, string> { [42] = "Иванов И.И." }))));

    private static AnnotationRow Rect(int id, int? author) =>
        new(id, 5, AnnotationKind.Rectangle, 10, 10, 60, 60, null, AnnotationColors.Red, author, DateTime.UtcNow);

    private static AnnotationRow Text(int id, string text, int? author) =>
        new(id, 5, AnnotationKind.Text, 100, 50, 100, 50, text, AnnotationColors.Yellow, author, DateTime.UtcNow);

    private IRenderedComponent<PhotoAnnotations> RenderAnnotations(bool canEdit, int width = 400, int height = 300) =>
        Render<PhotoAnnotations>(p => p
            .Add(x => x.AssetId, 5)
            .Add(x => x.NaturalWidth, width)
            .Add(x => x.NaturalHeight, height)
            .Add(x => x.CanEdit, canEdit)
            .AddChildContent("<img alt=\"фото\" />"));

    private IRenderedComponent<AssetCard> RenderCard(MediaAssetRow asset, bool viewOnly = false)
    {
        Render<MudPopoverProvider>();
        Details(asset, viewOnly: viewOnly);
        return Render<AssetCard>(p => p.Add(x => x.Id, 5));
    }

    private static AngleSharp.Dom.IElement SaveButton(IRenderedComponent<ImageEditor> cut) =>
        cut.FindAll("button").First(b => b.TextContent.Contains("Сохранить копию"));
}
