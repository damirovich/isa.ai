using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Bunit;
using ISC.AI.Abstractions.Application;
using ISC.AI.Modules.Media.Application;
using ISC.AI.Modules.Media.Application.Features.Scope;
using ISC.AI.Modules.Media.Application.Features.Settings;
using ISC.AI.Modules.Media.Application.Features.Verification;
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
/// Экраны режима одного оператора и настроек (ТФ-ВЕР-07, ТФ-ПЛ-08, ADR-0054): окно сличения с кнопками «Подтвердить
/// связь в досье» / «Отклонить» / «Неопределённо» и обязательным обоснованием; экран настроек — только чтение без права,
/// подсказка пределов и сохранение командой.
/// </summary>
public sealed class SingleOperatorUiTests : BunitContext, IAsyncLifetime
{
    private readonly IMediator _mediator = Substitute.For<IMediator>();
    private readonly IRenderedComponent<MudSnackbarProvider> _snackbars;

    public SingleOperatorUiTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices();
        Services.AddSingleton(_mediator);
        Services.AddSingleton(new MediaSearchOptions());
        SetRendererInfo(new RendererInfo("Server", true));

        // Итог сохранения настроек — всплывающим сообщением: его показывает поставщик сообщений, а не страница.
        _snackbars = Render<MudSnackbarProvider>();

        _mediator.Send(Arg.Any<GetCandidatePairQuery>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<ResponseDto<VerificationQueueItem>>(ResponseDto<VerificationQueueItem>.Ok(
                new VerificationQueueItem(21, 4, 3, 1, 11, 5, null, null, 0.61, null, "probe.jpg", new string('a', 64), null,
                    0, 1, CandidateStatus.Candidate, null))));
        _mediator.Send(Arg.Any<ListCasePersonsQuery>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<ResponseDto<IReadOnlyList<CasePersonItem>>>(
                ResponseDto<IReadOnlyList<CasePersonItem>>.Ok([new CasePersonItem(7, "Фигурант")])));
        _mediator.Send(Arg.Any<RecordVerificationCommand>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<ResponseDto<CandidateStatus>>(ResponseDto<CandidateStatus>.Ok(CandidateStatus.Confirmed)));
    }

    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;

    Task IAsyncLifetime.DisposeAsync() => base.DisposeAsync().AsTask();

    [Fact(DisplayName = "Окно сличения одного оператора: кнопки вместо выбора исхода; без обоснования неактивны; «Подтвердить» — с фигурантом")]
    public void Single_operator_window_uses_buttons()
    {
        GivenSettings(ConfirmationMode.SingleOperator);
        Render<MudPopoverProvider>();
        Services.GetRequiredService<NavigationManager>().NavigateTo("/media/verification/21?stage=Expert&person=7");
        var cut = Render<CandidateVerification>(p => p.Add(x => x.CandidateId, 21));

        cut.Markup.ShouldContain("решение оператора");
        cut.Markup.ShouldContain("ваше решение окончательное");
        cut.FindAll("input[type=radio]").ShouldBeEmpty();
        Button(cut, "Подтвердить связь в досье").HasAttribute("disabled").ShouldBeTrue();
        Button(cut, "Отклонить").HasAttribute("disabled").ShouldBeTrue();
        cut.Markup.ShouldContain("Заполните обоснование");

        cut.Find("textarea").Input("форма носа и ушей совпадает");
        Button(cut, "Подтвердить связь в досье").HasAttribute("disabled").ShouldBeFalse();
        Button(cut, "Подтвердить связь в досье").Click();

        cut.WaitForAssertion(() => _mediator.Received(1).Send(
            Arg.Is<RecordVerificationCommand>(c => c.CandidateId == 21 && c.Stage == VerificationStage.Expert
                && c.Verdict == VerificationVerdict.Confirmed && c.PersonRef == 7 && c.Rationale == "форма носа и ушей совпадает"),
            Arg.Any<CancellationToken>()));
        cut.WaitForAssertion(() => cut.Markup.ShouldContain(TwoPersonRule.ConfirmedMarker));
    }

    [Fact(DisplayName = "Режим двух лиц: прежняя форма — выбор исхода и «Записать решение», кнопок окна сличения нет")]
    public void Two_person_mode_keeps_verdict_form()
    {
        GivenSettings(ConfirmationMode.TwoPerson);
        Render<MudPopoverProvider>();
        Services.GetRequiredService<NavigationManager>().NavigateTo("/media/verification/21?stage=Expert");
        var cut = Render<CandidateVerification>(p => p.Add(x => x.CandidateId, 21));

        cut.Markup.ShouldContain("Решение стадии «эксперт»");
        cut.Markup.ShouldContain("Записать решение");
        cut.Markup.ShouldNotContain("Подтвердить связь в досье");
    }

    [Fact(DisplayName = "Экран настроек без права: значения видны, поля и «Сохранить» недоступны, объяснено почему")]
    public void Settings_page_is_read_only_without_right()
    {
        GivenSettings(ConfirmationMode.TwoPerson, canManage: false);
        var cut = Render<MediaSettingsPage>();

        cut.Markup.ShouldContain("менять их может только Администратор");
        cut.FindAll("button").ShouldNotContain(b => b.TextContent.Contains("Сохранить", StringComparison.Ordinal));
        cut.FindAll("input").Where(i => i.GetAttribute("type") is "number" or "text")
            .ShouldAllBe(i => i.HasAttribute("disabled"));
        cut.Markup.ShouldContain("балл 0,363 = 70 %");
    }

    [Fact(DisplayName = "Экран настроек: рабочий ниже границы — подсказка и «Сохранить» неактивна; верные значения уходят командой")]
    public void Settings_page_validates_and_saves()
    {
        GivenSettings(ConfirmationMode.SingleOperator, canManage: true);
        _mediator.Send(Arg.Any<SetMediaSettingsCommand>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<ResponseDto<MediaSettings>>(ResponseDto<MediaSettings>.Ok(
                new MediaSettings(ConfirmationMode.SingleOperator, 80, 72), "Настройки сохранены")));
        var cut = Render<MediaSettingsPage>();

        var fields = cut.FindAll("input").Where(i => i.GetAttribute("type") is "number" or "text").ToList();
        fields.Count.ShouldBe(2);
        fields[0].Change("60");
        cut.Markup.ShouldContain("Рабочий порог не может быть ниже нижней границы");
        Button(cut, "Сохранить").HasAttribute("disabled").ShouldBeTrue();

        cut.FindAll("input").Where(i => i.GetAttribute("type") is "number" or "text").First().Change("80");
        cut.FindAll("input").Where(i => i.GetAttribute("type") is "number" or "text").Last().Change("72");
        Button(cut, "Сохранить").Click();

        cut.WaitForAssertion(() => _mediator.Received(1).Send(
            Arg.Is<SetMediaSettingsCommand>(c => c.Mode == ConfirmationMode.SingleOperator && c.ThresholdPercent == 80 && c.MinThresholdPercent == 72),
            Arg.Any<CancellationToken>()));
        cut.WaitForAssertion(() => _snackbars.Markup.ShouldContain("Настройки сохранены"));
    }

    [Fact(DisplayName = "ADR-0062 v0.3: экран настроек — переключатель «Размечать дикторов» сохраняется сразу командой; без права недоступен")]
    public void Settings_page_toggles_transcript_speakers()
    {
        GivenSettings(ConfirmationMode.SingleOperator, canManage: true);
        _mediator.Send(Arg.Any<SetTranscriptSpeakersCommand>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<ResponseDto<MediaSettings>>(ResponseDto<MediaSettings>.Ok(
                MediaSettings.Defaults with { TranscriptSpeakers = false }, "Загруженные материалы будут расшифровываться без дикторов.")));
        var cut = Render<MediaSettingsPage>();

        cut.FindAll(".settings-savebar").ShouldBeEmpty(); // переключатель — без панели сохранения
        var toggle = cut.Find("input[type='checkbox']");
        toggle.HasAttribute("checked").ShouldBeTrue();
        toggle.Change(false);

        cut.WaitForAssertion(() => _mediator.Received(1).Send(
            Arg.Is<SetTranscriptSpeakersCommand>(c => !c.Enabled), Arg.Any<CancellationToken>()));
        cut.WaitForAssertion(() => _snackbars.Markup.ShouldContain("расшифровываться без дикторов"));

        GivenSettings(ConfirmationMode.SingleOperator, canManage: false);
        Render<MediaSettingsPage>().Find("input[type='checkbox']").HasAttribute("disabled").ShouldBeTrue();
    }

    [Fact(DisplayName = "Экран настроек: плитка режима — черновик; панель «Есть изменения» называет что изменено; «Отменить» возвращает сохранённое")]
    public void Settings_page_draft_and_discard()
    {
        GivenSettings(ConfirmationMode.SingleOperator, canManage: true);
        var cut = Render<MediaSettingsPage>();

        cut.FindAll(".settings-savebar").ShouldBeEmpty();
        var tiles = cut.FindAll("[role='radio']");
        tiles.Select(t => t.GetAttribute("aria-checked")).ShouldBe(["true", "false"]);

        tiles[1].Click(); // «Два лица»
        cut.FindAll("[role='radio']")[1].GetAttribute("aria-checked").ShouldBe("true");
        cut.Find(".settings-savebar").TextContent.ShouldContain("режим подтверждения");
        _mediator.DidNotReceive().Send(Arg.Any<SetMediaSettingsCommand>(), Arg.Any<CancellationToken>());

        Button(cut, "Отменить").Click();
        cut.FindAll(".settings-savebar").ShouldBeEmpty();
        cut.FindAll("[role='radio']")[0].GetAttribute("aria-checked").ShouldBe("true");
    }

    [Fact(DisplayName = "Экран настроек: кто и когда менял — именем; не менялись — «значения поставки»; без права плитки недоступны")]
    public void Settings_page_shows_who_changed()
    {
        _mediator.Send(Arg.Any<GetMediaSettingsQuery>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<ResponseDto<MediaSettingsView>>(ResponseDto<MediaSettingsView>.Ok(new MediaSettingsView(
                ConfirmationMode.SingleOperator, 70, 1, new DateTime(2026, 10, 22, 9, 5, 0, DateTimeKind.Utc), 7, false,
                "балл 0,363 = 70 %", UpdatedByName: "Петров П.П."))));
        var cut = Render<MediaSettingsPage>();

        cut.Find(".settings-meta").TextContent.ShouldContain("· Петров П.П.");
        cut.Find(".settings-meta").TextContent.ShouldContain("Изменено 22.10.2026");
        cut.FindAll("[role='radio']").ShouldAllBe(t => t.HasAttribute("disabled"));

        GivenSettings(ConfirmationMode.SingleOperator, canManage: true);
        Render<MediaSettingsPage>().Find(".settings-meta").TextContent.ShouldContain("действуют значения поставки");
    }

    [Fact(DisplayName = "Экран настроек: не загрузились — причина и «Повторить», которая запрашивает снова")]
    public void Settings_page_load_error_can_retry()
    {
        _mediator.Send(Arg.Any<GetMediaSettingsQuery>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<ResponseDto<MediaSettingsView>>(ResponseDto<MediaSettingsView>.BadRequest("Субъект не установлен.")));
        var cut = Render<MediaSettingsPage>();

        cut.Markup.ShouldContain("Настройки не загрузились: Субъект не установлен.");
        Button(cut, "Повторить").Click();
        _mediator.Received(2).Send(Arg.Any<GetMediaSettingsQuery>(), Arg.Any<CancellationToken>());
    }

    private static AngleSharp.Dom.IElement Button<T>(IRenderedComponent<T> cut, string text)
        where T : IComponent =>
        cut.FindAll("button").Single(b => b.TextContent.Contains(text, StringComparison.Ordinal));

    private void GivenSettings(ConfirmationMode mode, bool canManage = false) =>
        _mediator.Send(Arg.Any<GetMediaSettingsQuery>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<ResponseDto<MediaSettingsView>>(ResponseDto<MediaSettingsView>.Ok(new MediaSettingsView(
                mode, 85, 70, null, null, canManage, "балл 0,363 = 70 %"))));
}
