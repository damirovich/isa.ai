using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Bunit;
using ISC.AI.Abstractions.Application;
using ISC.AI.Modules.Media.Application;
using ISC.AI.Modules.Media.Application.Features.Import;
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

/// <summary>Экраны импорта архива (ADR-0041): отказ по праву, ненастроенные каталоги, задания, ход и остановка задания.</summary>
public sealed class MediaImportPageTests : BunitContext, IAsyncLifetime
{
    private readonly IMediator _mediator = Substitute.For<IMediator>();

    public MediaImportPageTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices();
        Services.AddSingleton(_mediator);
        SetRendererInfo(new RendererInfo("Server", true));
        _mediator.Send(Arg.Any<ListMediaImportJobsQuery>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<ResponseDto<IReadOnlyList<MediaImportJobSummary>>>(
                ResponseDto<IReadOnlyList<MediaImportJobSummary>>.Ok([Job(MediaImportJobStatus.Running)])));
    }

    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;

    Task IAsyncLifetime.DisposeAsync() => base.DisposeAsync().AsTask();

    [Fact(DisplayName = "Без права «Импорт архива» — отказ текстом сервера, ни каталогов, ни заданий")]
    public void No_permission_shows_server_refusal()
    {
        Setup(ResponseDto<MediaImportSetup>.BadRequest(MediaImportRules.NoPermissionMessage));

        var cut = Render<MediaImport>();

        cut.Markup.ShouldContain(MediaImportRules.NoPermissionMessage);
        cut.Markup.ShouldNotContain("Задания");
        _mediator.DidNotReceive().Send(Arg.Any<ListMediaImportJobsQuery>(), Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "Каталоги не настроены — подсказка с ключом настройки; исключённый каталог — с причиной; шагов импорта нет")]
    public void Without_roots_explains_configuration()
    {
        Setup(ResponseDto<MediaImportSetup>.Ok(new MediaImportSetup(
            [new MediaImportRoot(@"C:\Temp\архив", "пересекается с временным каталогом")], 1, 200L << 20, 16L << 30)));

        var cut = Render<MediaImport>();

        cut.Markup.ShouldContain(MediaImportOptions.RootsKey);
        cut.Markup.ShouldContain("пересекается с временным каталогом");
        cut.Markup.ShouldNotContain("1. Папка архива");
        cut.Markup.ShouldContain("Задания");
    }

    [Fact(DisplayName = "Есть каталог — шаги «папка» и «опись», список заданий со ссылкой на задание и счётчиками")]
    public void With_roots_shows_steps_and_jobs()
    {
        Setup(ResponseDto<MediaImportSetup>.Ok(new MediaImportSetup([new MediaImportRoot(@"D:\Архив")], 1, 200L << 20, 16L << 30)));
        _mediator.Send(Arg.Any<ListMediaImportFoldersQuery>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<ResponseDto<MediaImportFolderListing>>(ResponseDto<MediaImportFolderListing>.Ok(
                new MediaImportFolderListing(@"D:\Архив", ["2021", "2022"], false))));

        Render<MudPopoverProvider>();
        var cut = Render<MediaImport>();

        cut.Markup.ShouldContain("1. Папка архива");
        cut.Markup.ShouldContain("2. Опись");
        cut.Markup.ShouldContain("2021");
        cut.Find("a[href='/media/import/5']").TextContent.ShouldBe("5");
        cut.Markup.ShouldContain("принято 10 · дубликатов 2 · отказов 1");
        cut.FindAll("button").Single(b => b.TextContent.Contains("Проверить")).HasAttribute("disabled").ShouldBeTrue("без описи проверять нечего");
    }

    [Fact(DisplayName = "Проверка: ни один файл описи не найден — подсказка выбрать папку архива; в итоге видна папка проверки")]
    public void Check_hints_wrong_folder_when_nothing_found()
    {
        var plan = CheckWith(
            [],
            [new MediaImportProblem(new ManifestRow(2, @"А-1-26\фото.jpg", "А-1/26"), MediaImportPaths.NotFoundMessage),
             new MediaImportProblem(new ManifestRow(3, @"Х-99\фото.jpg", "Х-99/26"), MediaImportPaths.NotFoundMessage + "; дело № Х-99/26 не найдено среди доступных вам")]);

        var cut = RenderCheckedPage(plan);

        cut.Markup.ShouldContain(@"Папка архива: D:\Архив.");
        cut.Markup.ShouldContain(@"Ни один файл описи не найден в папке «D:\Архив»");
    }

    [Fact(DisplayName = "Проверка: часть файлов найдена — подсказки о папке нет, ошибки по строкам как есть")]
    public void Check_without_hint_when_some_files_found()
    {
        var item = new MediaImportItemDraft(2, "a.jpg", MediaImportFileKind.Image, "image/jpeg", 10, 5, "А-1/26", 1, 1,
            DateTimeOffset.Now, false, null, null, null);
        var plan = CheckWith([item], [new MediaImportProblem(new ManifestRow(3, "b.jpg", "А-1/26"), MediaImportPaths.NotFoundMessage)]);

        var cut = RenderCheckedPage(plan);

        cut.Markup.ShouldContain(MediaImportPaths.NotFoundMessage);
        cut.Markup.ShouldNotContain("Ни один файл описи не найден");
    }

    [Fact(DisplayName = "Страница задания: ход приёма и обработки, причина остановки; «Остановить» отправляет команду")]
    public void Job_page_shows_progress_and_stops()
    {
        _mediator.Send(Arg.Any<GetMediaImportJobQuery>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<ResponseDto<MediaImportJobDetails>>(ResponseDto<MediaImportJobDetails>.Ok(
                new MediaImportJobDetails(Job(MediaImportJobStatus.Running), new MediaImportItemPage(
                    [new MediaImportItemView(1, 3, "1/a.jpg", 10, "1/2023", 100, MediaImportItemStatus.Rejected, "файл не найден",
                        null, null, null, MediaImportProcessingState.None, null, null, null)], 1, 4)))));
        _mediator.Send(Arg.Any<StopMediaImportCommand>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<ResponseDto<bool>>(ResponseDto<bool>.Ok(true, "Задание остановлено.")));

        Render<MudPopoverProvider>();
        var cut = Render<MediaImportJob>(p => p.Add(x => x.JobId, 5));

        cut.Markup.ShouldContain("Приём файлов: 13 из 20");
        cut.Markup.ShouldContain("обработано 6, в обработке 1, ждут очереди 3");
        cut.Markup.ShouldContain("файл не найден");
        cut.Markup.ShouldContain("Ещё 4 строк — в делах вне вашего допуска");

        cut.FindAll("button").Single(b => b.TextContent.Contains("Остановить")).Click();

        _mediator.Received(1).Send(Arg.Is<StopMediaImportCommand>(c => c.JobId == 5), Arg.Any<CancellationToken>());
        cut.Markup.ShouldContain("Задание остановлено.");
    }

    private static MediaImportPlan CheckWith(IReadOnlyList<MediaImportItemDraft> items, IReadOnlyList<MediaImportProblem> problems) =>
        new(@"D:\Архив", "опись.csv", new string('B', 64), items, problems, [], 10, 0, 0, [], "UTF-8", "F");

    private IRenderedComponent<MediaImport> RenderCheckedPage(MediaImportPlan plan)
    {
        Setup(ResponseDto<MediaImportSetup>.Ok(new MediaImportSetup([new MediaImportRoot(@"D:\Архив")], 1, 200L << 20, 16L << 30)));
        _mediator.Send(Arg.Any<ListMediaImportFoldersQuery>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<ResponseDto<MediaImportFolderListing>>(ResponseDto<MediaImportFolderListing>.Ok(
                new MediaImportFolderListing(@"D:\Архив", [], false))));
        var manifest = new ManifestReadResult([], null, new string('B', 64), "UTF-8", ';', []);
        _mediator.Send(Arg.Any<CheckMediaImportQuery>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<ResponseDto<MediaImportCheck>>(ResponseDto<MediaImportCheck>.Ok(new MediaImportCheck(manifest, plan))));

        Render<MudPopoverProvider>();
        var cut = Render<MediaImport>();
        cut.FindComponent<Microsoft.AspNetCore.Components.Forms.InputFile>()
            .UploadFiles(InputFileContent.CreateFromText("файл;дело", "опись.csv"));
        cut.WaitForAssertion(() => cut.FindAll("button").Single(b => b.TextContent.Contains("Проверить")).HasAttribute("disabled").ShouldBeFalse());
        cut.FindAll("button").Single(b => b.TextContent.Contains("Проверить")).Click();
        cut.WaitForAssertion(() => cut.Markup.ShouldContain("3. Итог проверки"));
        return cut;
    }

    private void Setup(ResponseDto<MediaImportSetup> response) =>
        _mediator.Send(Arg.Any<GetMediaImportSetupQuery>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<ResponseDto<MediaImportSetup>>(response));

    private static MediaImportJobSummary Job(MediaImportJobStatus status) =>
        new(5, new DateTime(2026, 10, 13, 8, 0, 0, DateTimeKind.Utc), 7, @"D:\Архив\2021", "опись.csv", new string('A', 64),
            status, null, null, 20, 10, 2, 1, 2_000, 1_300, 3, 1, 6);
}
