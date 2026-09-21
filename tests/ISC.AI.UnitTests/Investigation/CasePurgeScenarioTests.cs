using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Enums;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.Media.Domain.Model;
using ISC.AI.Modules.Media.Domain.Services;
using ISC.AI.Profile.Investigation.Application.Features.Cases;
using ISC.AI.Profile.Investigation.Domain.Enums;
using ISC.AI.Profile.Investigation.Domain.Services;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Xunit;

namespace ISC.AI.UnitTests.Investigation;

/// <summary>
/// Уничтожение дела целиком (ADR-0025): право только у Администратора, подтверждение номером, акт в
/// журнале ДО удаления, общие носители не трогаются, документы сохраняются.
/// </summary>
/// <remarks>
/// Физическое удаление проверяется на настоящей базе в <c>CasePurgeTests</c>. Здесь — то, чего не видно
/// по отдельным хранилищам: кто вправе, что именно передаётся на удаление и в каком порядке, и что при
/// отказе на любом раннем шаге не удаляется ничего.
/// </remarks>
public sealed class CasePurgeScenarioTests
{
    private const int CaseId = 7;
    private const int UserId = 42;
    private const string Number = "12-2026/045";

    private readonly ICaseStore _cases = Substitute.For<ICaseStore>();
    private readonly IUserRoleStore _roles = Substitute.For<IUserRoleStore>();
    private readonly ISubjectProvider _subject = Substitute.For<ISubjectProvider>();
    private readonly IAccessContextProvider _access = Substitute.For<IAccessContextProvider>();
    private readonly IMediaPurger _purger = Substitute.For<IMediaPurger>();
    private readonly IAuditWriter _audit = Substitute.For<IAuditWriter>();
    private readonly List<string> _calls = [];

    public CasePurgeScenarioTests()
    {
        _subject.GetCurrentUserIdAsync(Arg.Any<CancellationToken>()).Returns((int?)UserId);
        _roles.GetRoleAsync(UserId, Arg.Any<CancellationToken>()).Returns(InvestigationRole.Administrator);
        _access.GetCurrentAsync(Arg.Any<CancellationToken>())
            .Returns(new AccessContext(UserId.ToString(CultureInfo.InvariantCulture), 3, [5]));

        // Дело: носители 11 и 12 — только его, 13 — общий с другим делом (дедупликация по хешу).
        _cases.GetCompositionAsync(CaseId, Arg.Any<AccessContext>(), Arg.Any<CancellationToken>())
            .Returns(new CaseComposition(
                CaseId, Number, Classification: 2, DivisionId: 5,
                ExclusiveAssetIds: [11, 12], SharedAssetIds: [13],
                Persons: 2, ReferencePhotos: 1, Appearances: 3, Authorizations: 1, DocumentLinks: 4));
        _cases.PurgeAsync(CaseId, Arg.Any<AccessContext>(), Arg.Any<CancellationToken>())
            .Returns(CaseWriteResult.Ok)
            .AndDoes(_ => _calls.Add("case"));

        _purger.PurgeAsync(Arg.Any<int>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(new MediaPurgeResult(true, FacesRemoved: 5, FilesRemoved: 6))
            .AndDoes(ci => _calls.Add("asset:" + ci.ArgAt<int>(0).ToString(CultureInfo.InvariantCulture)));
        _purger.PurgeCaseSearchesAsync(CaseId, Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(new CaseSearchPurgeResult(SessionsRemoved: 2, CandidatesRemoved: 40, ProbeFilesRemoved: 1))
            .AndDoes(_ => _calls.Add("searches"));
        _audit.When(a => a.WriteAsync(Arg.Any<AuditEntry>(), Arg.Any<CancellationToken>()))
            .Do(_ => _calls.Add("act"));
    }

    [Fact(DisplayName = "ADR-0025: акт — первым; уничтожаются только носители этого дела, общий носитель остаётся; затем поиски и само дело")]
    public async Task Administrator_destroys_case_in_fail_closed_order()
    {
        var response = await SendAsync(Number, "Решение руководителя от 20.09.2026 № 17");

        response.Status.ShouldBeTrue();
        _calls.ShouldBe(["act", "asset:11", "asset:12", "searches", "case"]);

        // Общий носитель 13 — материал другого дела: уничтожать его нельзя ни при каких условиях.
        await _purger.DidNotReceive().PurgeAsync(13, Arg.Any<int?>(), Arg.Any<CancellationToken>());

        var summary = response.Data.ShouldNotBeNull();
        summary.AssetsRemoved.ShouldBe(2);
        summary.AssetsKeptShared.ShouldBe(1);
        summary.FacesRemoved.ShouldBe(10);
        summary.FilesRemoved.ShouldBe(13); // 6 + 6 по носителям + 1 проба
        summary.SessionsRemoved.ShouldBe(2);
        summary.DocumentsKept.ShouldBe(4);
    }

    [Fact(DisplayName = "Акт в журнале: номер, основание, состав уничтожаемого и что сохранено; гриф и подразделение дела")]
    public async Task Act_records_full_composition_and_reason()
    {
        await SendAsync(Number, "Истечение срока хранения, приказ № 3");

        await _audit.Received(1).WriteAsync(
            Arg.Is<AuditEntry>(e =>
                e.Action == AuditAction.Purge
                && e.ObjectRef == "investigation:case:7:destroy"
                && e.SubjectId == UserId
                && e.Classification == 2
                && e.DivisionId == 5
                && e.PayloadSensitive!.Contains(Number, StringComparison.Ordinal)
                && e.PayloadSensitive.Contains("Истечение срока хранения", StringComparison.Ordinal)
                && e.PayloadSensitive.Contains("документы документооборота 4", StringComparison.Ordinal)
                && e.PayloadSensitive.Contains("[11,12]", StringComparison.Ordinal)
                && e.PayloadSensitive.Contains("[13]", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
    }

    [Theory(DisplayName = "ADR-0025: уничтожать может ТОЛЬКО Администратор — остальные и субъект без роли получают отказ, ничего не читается и не удаляется")]
    [InlineData(InvestigationRole.Investigator)]
    [InlineData(InvestigationRole.Head)]
    [InlineData(InvestigationRole.FaceExpert)]
    [InlineData(InvestigationRole.Verifier)]
    [InlineData(InvestigationRole.SecurityOfficer)]
    [InlineData(null)]
    public async Task Only_administrator_may_destroy(InvestigationRole? role)
    {
        _roles.GetRoleAsync(UserId, Arg.Any<CancellationToken>()).Returns(role);

        // Режима первичной настройки у уничтожения нет: даже если Администратора ещё нет — отказ.
        _roles.AnyAdministratorAsync(Arg.Any<CancellationToken>()).Returns(false);

        var response = await SendAsync(Number, "Решение руководителя № 17");

        response.Status.ShouldBeFalse();
        response.StatusMessage.ShouldBe("Уничтожение дела доступно только Администратору.");
        _calls.ShouldBeEmpty();
        await _cases.DidNotReceive().GetCompositionAsync(Arg.Any<int>(), Arg.Any<AccessContext>(), Arg.Any<CancellationToken>());
    }

    [Theory(DisplayName = "Неверно введённый номер — отказ, ни акта, ни удаления")]
    [InlineData("12-2026/046")]
    [InlineData("12-2026/04")]
    [InlineData("")]
    public async Task Wrong_confirmation_number_destroys_nothing(string typed)
    {
        var response = await SendAsync(typed, "Решение руководителя № 17");

        response.Status.ShouldBeFalse();
        response.StatusMessage.ShouldContain("не совпадает");
        _calls.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Номер с пробелами по краям принимается: оператор скопировал его из карточки")]
    public async Task Confirmation_number_is_trimmed()
    {
        var response = await SendAsync("  " + Number + " ", "Решение руководителя № 17");

        response.Status.ShouldBeTrue();
    }

    [Fact(DisplayName = "Недоступное или несуществующее дело — «не найдено», неотличимо (ТБ-020/021), ничего не удаляется")]
    public async Task Inaccessible_case_is_not_found()
    {
        _cases.GetCompositionAsync(CaseId, Arg.Any<AccessContext>(), Arg.Any<CancellationToken>())
            .Returns((CaseComposition?)null);

        var response = await SendAsync(Number, "Решение руководителя № 17");

        response.StatusCode.ShouldBe(ResponseStatusCode.NotFound);
        _calls.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Журнал недоступен — акт не записан, и НИЧЕГО не удалено (fail-closed, ТБ-064)")]
    public async Task Unavailable_journal_destroys_nothing()
    {
        _audit.WriteAsync(Arg.Any<AuditEntry>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("журнал недоступен"));

        await Should.ThrowAsync<InvalidOperationException>(() => SendAsync(Number, "Решение руководителя № 17"));

        await _purger.DidNotReceive().PurgeAsync(Arg.Any<int>(), Arg.Any<int?>(), Arg.Any<CancellationToken>());
        await _purger.DidNotReceive().PurgeCaseSearchesAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<CancellationToken>());
        await _cases.DidNotReceive().PurgeAsync(Arg.Any<int>(), Arg.Any<AccessContext>(), Arg.Any<CancellationToken>());
    }

    [Theory(DisplayName = "Валидатор: номер и основание обязательны; основание — с реквизитами, не короче 10 символов")]
    [InlineData(7, "12-2026/045", "Решение руководителя № 17", true)]
    [InlineData(7, "", "Решение руководителя № 17", false)]
    [InlineData(7, "12-2026/045", "", false)]
    [InlineData(7, "12-2026/045", "по решению", true)]
    [InlineData(7, "12-2026/045", "решение", false)]
    [InlineData(0, "12-2026/045", "Решение руководителя № 17", false)]
    public void Validator_requires_number_and_meaningful_reason(int caseId, string number, string reason, bool valid) =>
        new PurgeCaseValidator().Validate(new PurgeCaseCommand(caseId, number, reason)).IsValid.ShouldBe(valid);

    private async Task<ResponseDto<CasePurgeSummary>> SendAsync(string typedNumber, string reason)
    {
        var handler = new PurgeCaseCommand.Handler(_cases, _roles, _subject, _access, _purger, _audit);
        return await handler.Handle(new PurgeCaseCommand(CaseId, typedNumber, reason), CancellationToken.None);
    }
}
