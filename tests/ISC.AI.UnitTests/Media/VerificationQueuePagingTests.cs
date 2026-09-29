using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.Media.Application.Features.Verification;
using ISC.AI.Modules.Media.Domain.Model;
using ISC.AI.Modules.Media.Domain.Services;
using NSubstitute;
using Shouldly;
using Xunit;

namespace ISC.AI.UnitTests.Media;

/// <summary>
/// Постраничная очередь верификации (ТФ-ВЕР-01): сервер отдаёт одну страницу и общее число кандидатов в очереди,
/// границы страницы зажимаются и валидатором, и обработчиком — очередь целиком за один запрос не отдаётся.
/// </summary>
public sealed class VerificationQueuePagingTests
{
    private const int Expert = 3;

    private readonly ISubjectProvider _subjects = Substitute.For<ISubjectProvider>();
    private readonly IVerificationPolicy _policy = Substitute.For<IVerificationPolicy>();
    private readonly IAccessContextProvider _access = Substitute.For<IAccessContextProvider>();
    private readonly ICaseScope _caseScope = Substitute.For<ICaseScope>();
    private readonly ISearchSessionStore _store = Substitute.For<ISearchSessionStore>();

    public VerificationQueuePagingTests()
    {
        _subjects.GetCurrentUserIdAsync(Arg.Any<CancellationToken>()).Returns(Expert);
        _policy.CanActAsync(VerificationStage.Expert, Expert, Arg.Any<CancellationToken>()).Returns(true);
        _access.GetCurrentAsync(Arg.Any<CancellationToken>()).Returns(new AccessContext("3", 2, [1]));
        _caseScope.ListAccessibleCasesAsync(Arg.Any<AccessContext>(), Arg.Any<CancellationToken>())
            .Returns([new CaseScopeItem(3, "№ 1", "Дело", 2, 1)]);
        _store.ListQueuePageAsync(Arg.Any<VerificationStage>(), Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<int>(), Arg.Any<int>(),
                Arg.Any<AccessContext>(), Arg.Any<CancellationToken>())
            .Returns(new VerificationQueuePage([Candidate(21), Candidate(22)], 130));
        _store.GetAsync(5, Arg.Any<AccessContext>(), Arg.Any<CancellationToken>()).Returns(Session());
    }

    [Fact(DisplayName = "Страница 3 по 24: хранилище получает skip=48, take=24; TotalCount — число всей очереди, а не страницы")]
    public async Task Page_is_translated_to_skip_take_and_total_comes_from_store()
    {
        var response = await Handler().Handle(new ListVerificationQueueQuery(VerificationStage.Expert, 3, 24), CancellationToken.None);

        response.Status.ShouldBeTrue();
        response.Data!.Count.ShouldBe(2);
        response.TotalCount.ShouldBe(130);
        await _store.Received(1).ListQueuePageAsync(
            VerificationStage.Expert, Arg.Any<IReadOnlyCollection<int>>(), 48, 24, Arg.Any<AccessContext>(), Arg.Any<CancellationToken>());
        // Сессия запрошена один раз на всю страницу, а не на каждого кандидата.
        await _store.Received(1).GetAsync(5, Arg.Any<AccessContext>(), Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "Обработчик зажимает границы в обход валидатора: страница < 1 → первая, размер > 100 → 100")]
    public async Task Handler_clamps_page_bounds()
    {
        await Handler().Handle(new ListVerificationQueueQuery(VerificationStage.Expert, 0, 5000), CancellationToken.None);

        await _store.Received(1).ListQueuePageAsync(
            VerificationStage.Expert, Arg.Any<IReadOnlyCollection<int>>(), 0, ListVerificationQueueQuery.MaxPageSize,
            Arg.Any<AccessContext>(), Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "Пустая область дел — пустая очередь с нулевым итогом, хранилище не читается (ТБ-071)")]
    public async Task Empty_case_scope_returns_empty_page()
    {
        _caseScope.ListAccessibleCasesAsync(Arg.Any<AccessContext>(), Arg.Any<CancellationToken>()).Returns([]);

        var response = await Handler().Handle(new ListVerificationQueueQuery(VerificationStage.Expert), CancellationToken.None);

        response.Data!.ShouldBeEmpty();
        response.TotalCount.ShouldBe(0);
        await _store.DidNotReceiveWithAnyArgs().ListQueuePageAsync(default, default!, default, default, default!, default);
    }

    [Fact(DisplayName = "Валидатор: страница ≥ 1, размер 1..100, известная стадия; сводка аудита называет страницу")]
    public void Validator_checks_bounds_and_audit_names_page()
    {
        var validator = new ListVerificationQueueValidator();

        validator.Validate(new ListVerificationQueueQuery(VerificationStage.Verifier)).IsValid.ShouldBeTrue();
        validator.Validate(new ListVerificationQueueQuery(VerificationStage.Expert, 2, 100)).IsValid.ShouldBeTrue();
        validator.Validate(new ListVerificationQueueQuery(VerificationStage.Expert, 0, 24)).IsValid.ShouldBeFalse();
        validator.Validate(new ListVerificationQueueQuery(VerificationStage.Expert, 1, 0)).IsValid.ShouldBeFalse();
        validator.Validate(new ListVerificationQueueQuery(VerificationStage.Expert, 1, 101)).IsValid.ShouldBeFalse();
        validator.Validate(new ListVerificationQueueQuery((VerificationStage)42)).IsValid.ShouldBeFalse();

        new ListVerificationQueueQuery(VerificationStage.Expert, 3).AuditSummary.ShouldBe("media:verification:queue:Expert:page:3");
    }

    private ListVerificationQueueQuery.Handler Handler() => new(_subjects, _policy, _access, _caseScope, _store);

    private static SearchCandidateRow Candidate(int id) =>
        new(Id: id, SessionId: 5, CaseId: 3, Rank: id, FaceId: 100 + id, AssetId: 50, FrameIndex: null, FrameTimestampMs: null,
            CosineDistance: 0.2, CropStoredFileName: "crop.jpg", ModelVersion: "sface-1", Classification: 2, DivisionId: 1,
            Status: CandidateStatus.Candidate, PersonRef: null, Decisions: []);

    private static SearchSessionRow Session() =>
        new(Id: 5, CaseId: 3, AuthorizationRef: "Постановление", Scope: SearchScopeKind.CurrentCase, CaseIds: [3],
            ProbeSha256: "abc", ProbeFaceId: 900, ProbeCropStoredFileName: "probe.jpg", TopK: 20, MaxCosineDistance: null,
            DetectorVersion: "yunet-1", EmbedderVersion: "sface-1", Classification: 2, DivisionId: 1, RequestedByUserId: 1,
            CreatedAt: DateTime.UtcNow, CandidateCount: 2);
}
