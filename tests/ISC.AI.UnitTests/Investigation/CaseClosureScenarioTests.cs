using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.Media.Domain.Model;
using ISC.AI.Modules.Media.Domain.Services;
using ISC.AI.Profile.Investigation.Application.Features.Cases;
using ISC.AI.Profile.Investigation.Domain.Enums;
using ISC.AI.Profile.Investigation.Domain.Services;
using NSubstitute;
using Shouldly;
using Xunit;

namespace ISC.AI.UnitTests.Investigation;

/// <summary>
/// Закрытие дела исполняет регламент удаления биометрических шаблонов с актом (ТФ-ДЕЛ-04, ТБ-074).
/// </summary>
/// <remarks>
/// Проверяется связка, которую нельзя увидеть по отдельным частям: смена статуса → снятие биометрии с
/// носителей ИМЕННО этого дела → акт. Здесь же закреплено поведение при сбое: дело закрыто, биометрия
/// не снята — сценарий обязан сказать об этом явно и НЕ писать акт, иначе акт удостоверял бы
/// несостоявшееся уничтожение. Само удаление проверяется на настоящей базе в <c>MediaPurgeTests</c>.
/// </remarks>
public sealed class CaseClosureScenarioTests
{
    private const int CaseId = 7;
    private const int UserId = 42;

    private readonly ICaseStore _cases = Substitute.For<ICaseStore>();
    private readonly IUserRoleStore _roles = Substitute.For<IUserRoleStore>();
    private readonly ISubjectProvider _subject = Substitute.For<ISubjectProvider>();
    private readonly IAccessContextProvider _access = Substitute.For<IAccessContextProvider>();
    private readonly IMediaPurger _purger = Substitute.For<IMediaPurger>();

    public CaseClosureScenarioTests()
    {
        _subject.GetCurrentUserIdAsync(Arg.Any<CancellationToken>()).Returns((int?)UserId);
        _access.GetCurrentAsync(Arg.Any<CancellationToken>())
            .Returns(new AccessContext(UserId.ToString(System.Globalization.CultureInfo.InvariantCulture), 2, [5]));
        _roles.GetRoleAsync(UserId, Arg.Any<CancellationToken>()).Returns(InvestigationRole.Investigator);
        _cases.SetStatusAsync(CaseId, Arg.Any<CaseStatus>(), Arg.Any<AccessContext>(), Arg.Any<CancellationToken>())
            .Returns(CaseWriteResult.Ok);
        _cases.ListMediaAssetIdsAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>())
            .Returns(new List<int> { 11, 12, 13 });
        _purger.PurgeTemplatesAsync(
                Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(new TemplatePurgeResult(AssetsAffected: 2, TemplatesRemoved: 9, CropsRemoved: 9));
    }

    [Fact(DisplayName = "ТФ-ДЕЛ-04: закрытие снимает биометрию с носителей дела и записывает акт с числами регламента")]
    public async Task Closing_purges_templates_and_writes_act()
    {
        var response = await SendAsync(CaseStatus.Closed);

        response.Status.ShouldBeTrue();

        // Снимается биометрия ровно с носителей ЭТОГО дела — не «со всех», не «по подразделению».
        await _purger.Received(1).PurgeTemplatesAsync(
            Arg.Is<IReadOnlyCollection<int>>(ids => ids.Count == 3),
            Arg.Is<string>(reason => reason.Contains(CaseId.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)),
            UserId,
            Arg.Any<CancellationToken>());

        // Акт повторяет фактические числа регламента: он и есть подтверждение для надзора.
        await _cases.Received(1).SaveClosureActAsync(
            Arg.Is<CaseClosureActDraft>(act =>
                act.CaseId == CaseId
                && act.ExecutedByUserId == UserId
                && act.MediaAssetsTotal == 3
                && act.AssetsAffected == 2
                && act.TemplatesRemoved == 9
                && act.CropsRemoved == 9),
            Arg.Any<CancellationToken>());
    }

    [Theory(DisplayName = "Смена статуса, не являющаяся закрытием, биометрию не трогает")]
    [InlineData(CaseStatus.InProgress)]
    [InlineData(CaseStatus.Suspended)]
    public async Task Other_statuses_keep_biometrics(CaseStatus status)
    {
        var response = await SendAsync(status);

        response.Status.ShouldBeTrue();
        await _purger.DidNotReceive().PurgeTemplatesAsync(
            Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<CancellationToken>());
        await _cases.DidNotReceive().SaveClosureActAsync(Arg.Any<CaseClosureActDraft>(), Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "Дело недоступно — ни статуса, ни регламента: чужое дело нельзя закрыть перебором номера")]
    public async Task Inaccessible_case_is_not_purged()
    {
        _cases.SetStatusAsync(CaseId, CaseStatus.Closed, Arg.Any<AccessContext>(), Arg.Any<CancellationToken>())
            .Returns(CaseWriteResult.NotFound);

        var response = await SendAsync(CaseStatus.Closed);

        response.Status.ShouldBeFalse();
        await _purger.DidNotReceive().PurgeTemplatesAsync(
            Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "Регламент не исполнен (журнал аудита недоступен) — явная ошибка и НЕТ акта")]
    public async Task Failed_purge_is_reported_and_leaves_no_act()
    {
        _purger.PurgeTemplatesAsync(
                Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns<TemplatePurgeResult>(_ => throw new InvalidOperationException("журнал недоступен"));

        var response = await SendAsync(CaseStatus.Closed);

        // Дело уже закрыто (статус сменён первым), но «успешно» отвечать нельзя: биометрия осталась.
        response.Status.ShouldBeFalse();
        response.StatusMessage.ShouldContain("регламент");
        await _cases.DidNotReceive().SaveClosureActAsync(Arg.Any<CaseClosureActDraft>(), Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "ADR-0024 (поставка по умолчанию): шаблоны ХРАНЯТСЯ — закрытие ничего не удаляет и акта не пишет")]
    public async Task Closing_keeps_templates_when_regulation_is_off()
    {
        // Решение заказчика: человек из оконченного дела должен находиться по новому делу, поэтому
        // биометрия живёт, пока живёт дело. Закрытые дела в поиск при этом сами не попадают — область
        // расширяет оператор (ТФ-ПЛ-05).
        var response = await SendAsync(CaseStatus.Closed, purgeOnClosure: false);

        response.Status.ShouldBeTrue();
        await _purger.DidNotReceive().PurgeTemplatesAsync(
            Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<CancellationToken>());
        await _cases.DidNotReceive().SaveClosureActAsync(Arg.Any<CaseClosureActDraft>(), Arg.Any<CancellationToken>());
    }

    private async Task<ISC.AI.Abstractions.Application.ResponseDto<bool>> SendAsync(
        CaseStatus status, bool purgeOnClosure = true)
    {
        var handler = new SetCaseStatusCommand.Handler(
            _cases, _roles, _subject, _access, _purger, new InvestigationRetentionOptions(purgeOnClosure));
        return await handler.Handle(new SetCaseStatusCommand(CaseId, status), CancellationToken.None);
    }
}
