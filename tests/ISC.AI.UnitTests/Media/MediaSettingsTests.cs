using System.Threading;
using System.Threading.Tasks;
using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.Media.Application;
using ISC.AI.Modules.Media.Application.Features.Settings;
using ISC.AI.Modules.Media.Domain.Model;
using ISC.AI.Modules.Media.Domain.Services;
using NSubstitute;
using Shouldly;
using Xunit;

namespace ISC.AI.UnitTests.Media;

/// <summary>
/// Настройки поиска и верификации (ТФ-ВЕР-07, ТФ-ПЛ-08, ТФ-АДМ-03, ADR-0054): пределы 40–99 %, рабочий не ниже границы,
/// граница — на сервере; менять — только с правом, каждое изменение и отказ — в журнале с прежними и новыми значениями.
/// </summary>
public sealed class MediaSettingsTests
{
    private readonly IMediaSettingsStore _store = Substitute.For<IMediaSettingsStore>();
    private readonly IMediaAdministration _administration = Substitute.For<IMediaAdministration>();
    private readonly ISubjectProvider _subjects = Substitute.For<ISubjectProvider>();
    private readonly IAuditWriter _audit = Substitute.For<IAuditWriter>();

    public MediaSettingsTests()
    {
        _subjects.GetCurrentUserIdAsync(Arg.Any<CancellationToken>()).Returns(7);
        _administration.CanManageSettingsAsync(Arg.Any<CancellationToken>()).Returns(true);
        _store.GetAsync(Arg.Any<CancellationToken>()).Returns(
            MediaSettings.Defaults,
            new MediaSettings(ConfirmationMode.TwoPerson, 80, 75));
    }

    [Fact(DisplayName = "Умолчания поставки: один оператор, рабочий порог 85 %, нижняя граница 70 % [АИС В-05]")]
    public void Defaults_follow_customer_decision()
    {
        MediaSettings.Defaults.Mode.ShouldBe(ConfirmationMode.SingleOperator);
        MediaSettings.Defaults.ThresholdPercent.ShouldBe(85);
        MediaSettings.Defaults.MinThresholdPercent.ShouldBe(70);
    }

    [Theory(DisplayName = "Проверка значений: каждый порог 40–99 %, рабочий не ниже границы; иначе — отказ с пояснением")]
    [InlineData(85, 70, true)]
    [InlineData(40, 40, true)]
    [InlineData(99, 99, true)]
    [InlineData(39, 39, false)]
    [InlineData(100, 70, false)]
    [InlineData(85, 30, false)]
    [InlineData(70, 85, false)]
    public void Validate_ranges(int threshold, int min, bool valid)
    {
        var error = MediaSettings.Validate(ConfirmationMode.SingleOperator, threshold, min);
        (error is null).ShouldBe(valid);
        new SetMediaSettingsValidator().Validate(new SetMediaSettingsCommand(ConfirmationMode.SingleOperator, threshold, min))
            .IsValid.ShouldBe(valid);
        MediaSettings.Validate((ConfirmationMode)9, 85, 70).ShouldNotBeNull();
    }

    [Fact(DisplayName = "Порог поиска: не задан — рабочий; ниже границы — граница; выше — как задал оператор")]
    public void Effective_percent_respects_bound()
    {
        var settings = new MediaSettings(ConfirmationMode.SingleOperator, 85, 70);
        settings.EffectivePercent(null).ShouldBe(85);
        settings.EffectivePercent(50).ShouldBe(70);
        settings.EffectivePercent(92).ShouldBe(92);
    }

    [Fact(DisplayName = "Режим строкой: туда и обратно; мусор — «не задано»")]
    public void Mode_text_round_trip()
    {
        MediaSettings.ParseMode(MediaSettings.ModeText(ConfirmationMode.SingleOperator)).ShouldBe(ConfirmationMode.SingleOperator);
        MediaSettings.ParseMode(MediaSettings.ModeText(ConfirmationMode.TwoPerson)).ShouldBe(ConfirmationMode.TwoPerson);
        MediaSettings.ParseMode(" Two-Person ").ShouldBe(ConfirmationMode.TwoPerson);
        MediaSettings.ParseMode("двое").ShouldBeNull();
        MediaSettings.ParseMode(null).ShouldBeNull();
    }

    [Fact(DisplayName = "Без права настроек — отказ, ничего не сохраняется, попытка — в журнале")]
    public async Task Denied_without_right_is_audited()
    {
        _administration.CanManageSettingsAsync(Arg.Any<CancellationToken>()).Returns(false);

        var response = await SetAsync(new SetMediaSettingsCommand(ConfirmationMode.TwoPerson, 80, 75));

        response.StatusCode.ShouldBe(ResponseStatusCode.BadRequest);
        response.StatusMessage.ShouldBe(SetMediaSettingsCommand.DeniedMessage);
        await _store.DidNotReceiveWithAnyArgs().SaveAsync(default, default, default, default, default);
        await _audit.Received(1).WriteAsync(
            Arg.Is<AuditEntry>(e => e.Action == AuditAction.Modify && e.ObjectRef == "media:settings" && e.SubjectId == 7
                && e.PayloadSensitive!.Contains("изменение отклонено") && e.PayloadSensitive.Contains("два лица")),
            Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "Значение вне пределов отклоняет хранилище — отказ с его пояснением, журнал изменений не пишется")]
    public async Task Store_validation_error_is_returned()
    {
        _store.SaveAsync(ConfirmationMode.SingleOperator, 100, 70, 7, Arg.Any<CancellationToken>()).Returns("Порог схожести задаётся от 40 до 99 %.");

        var response = await SetAsync(new SetMediaSettingsCommand(ConfirmationMode.SingleOperator, 100, 70));

        response.StatusCode.ShouldBe(ResponseStatusCode.BadRequest);
        response.StatusMessage.ShouldContain("от 40 до 99 %");
        await _audit.DidNotReceiveWithAnyArgs().WriteAsync(default!, default);
    }

    [Fact(DisplayName = "Сохранение: «было → стало» по режиму, рабочему порогу и границе — в журнале; ответ — действующие значения")]
    public async Task Change_is_audited_with_before_and_after()
    {
        _store.SaveAsync(ConfirmationMode.TwoPerson, 80, 75, 7, Arg.Any<CancellationToken>()).Returns((string?)null);

        var response = await SetAsync(new SetMediaSettingsCommand(ConfirmationMode.TwoPerson, 80, 75));

        response.Status.ShouldBeTrue();
        response.Data!.Mode.ShouldBe(ConfirmationMode.TwoPerson);
        await _audit.Received(1).WriteAsync(
            Arg.Is<AuditEntry>(e => e.Action == AuditAction.Modify && e.ObjectRef == "media:settings"
                && e.PayloadSensitive == "режим подтверждения: один оператор → два лица; рабочий порог: 85 → 80 %; нижняя граница: 70 → 75 %"),
            Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "Чтение настроек: значения, право менять и шкала словами; без субъекта — отказ")]
    public async Task Get_returns_view_and_requires_subject()
    {
        var handler = new GetMediaSettingsQuery.Handler(_store, _administration, _subjects, new MediaSearchOptions());

        var view = (await handler.Handle(new GetMediaSettingsQuery(), CancellationToken.None)).Data!;
        view.Mode.ShouldBe(ConfirmationMode.SingleOperator);
        view.ThresholdPercent.ShouldBe(85);
        view.MinThresholdPercent.ShouldBe(70);
        view.CanManage.ShouldBeTrue();
        view.ScaleDescription.ShouldBe("балл 0,363 = 70 %");

        _subjects.GetCurrentUserIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);
        (await handler.Handle(new GetMediaSettingsQuery(), CancellationToken.None)).Status.ShouldBeFalse();
    }

    private async Task<ResponseDto<MediaSettings>> SetAsync(SetMediaSettingsCommand command) =>
        await new SetMediaSettingsCommand.Handler(_store, _administration, _subjects, _audit).Handle(command, CancellationToken.None);
}
