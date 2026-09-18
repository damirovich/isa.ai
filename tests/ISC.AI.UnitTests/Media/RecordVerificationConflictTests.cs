using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.Media.Application.Features.Verification;
using ISC.AI.Modules.Media.Domain.Model;
using ISC.AI.Modules.Media.Domain.Services;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Xunit;

namespace ISC.AI.UnitTests.Media;

/// <summary>
/// Гонка двух одновременных решений одной стадии (ТБ-073): хранилище отказывает по уникальному индексу,
/// обработчик отвечает конфликтом, попытка аудируется, появление фигуранта не создаётся.
/// </summary>
public sealed class RecordVerificationConflictTests
{
    [Fact(DisplayName = "Второе одновременное решение стадии → Conflict, аудит decision-denied, появление не создаётся")]
    public async Task Concurrent_decision_is_reported_as_conflict()
    {
        var subjects = Substitute.For<ISubjectProvider>();
        subjects.GetCurrentUserIdAsync(Arg.Any<CancellationToken>()).Returns(7);
        var policy = Substitute.For<IVerificationPolicy>();
        policy.CanActAsync(Arg.Any<VerificationStage>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(true);
        var accessProvider = Substitute.For<IAccessContextProvider>();
        accessProvider.GetCurrentAsync(Arg.Any<CancellationToken>()).Returns(new AccessContext("7", 2, [1]));
        var store = Substitute.For<ISearchSessionStore>();
        store.GetCandidateAsync(11, Arg.Any<AccessContext>(), Arg.Any<CancellationToken>()).Returns(
            new SearchCandidateRow(11, 5, 1, 1, 100, 200, null, null, 0.2, "crop.jpg", "sface-test", 1, 1,
                CandidateStatus.PendingVerifier, 42,
                [new VerificationDecision(3, VerificationStage.Expert, VerificationVerdict.Confirmed, "признаки", DateTime.UtcNow)]));
        store.RecordDecisionAsync(11, Arg.Any<VerificationDecision>(), Arg.Any<CandidateStatus>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Решение этой стадии по кандидату уже записано другим сотрудником (ТБ-073)."));
        var caseScope = Substitute.For<ICaseScope>();
        var audit = Substitute.For<IAuditWriter>();

        var response = await new RecordVerificationCommand.Handler(subjects, policy, accessProvider, store, caseScope, audit)
            .Handle(new RecordVerificationCommand(11, VerificationStage.Verifier, VerificationVerdict.Confirmed, "признаки"), CancellationToken.None);

        response.Status.ShouldBeFalse();
        response.StatusCode.ShouldBe(ResponseStatusCode.Conflict);
        response.StatusMessage.ShouldContain("ТБ-073");
        await audit.Received(1).WriteAsync(
            Arg.Is<AuditEntry>(e => e.ObjectRef == "media:candidate:11:decision-denied"), Arg.Any<CancellationToken>());
        await caseScope.DidNotReceive().RecordAppearanceAsync(Arg.Any<ConfirmedAppearance>(), Arg.Any<CancellationToken>());
    }
}
