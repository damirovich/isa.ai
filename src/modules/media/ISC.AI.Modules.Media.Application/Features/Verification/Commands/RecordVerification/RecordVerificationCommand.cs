using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.Media.Domain.Model;
using ISC.AI.Modules.Media.Domain.Services;
using Mediator;

namespace ISC.AI.Modules.Media.Application.Features.Verification;

/// <summary>
/// Записать решение стадии верификации по кандидату (ТФ-ВЕР-01..03, ТБ-073, GATE-5). Статус кандидата
/// считается ТОЛЬКО правилом двух лиц (<see cref="TwoPersonRule"/>) по решениям людей; никакого
/// автоматического решения нет. Два «подтверждён» РАЗНЫХ сотрудников → «следственная версия — требует
/// процессуальной проверки» и появление фигуранта в деле (ТФ-ПЕР-02).
/// </summary>
/// <remarks>
/// НЕ <see cref="IAuditableRequest"/>: запись ТБ-072 требует ОБОИХ субъектов при подтверждении и отдельной
/// фиксации ОТКЛОНЁННЫХ попыток (самоподтверждение, повтор стадии, чужая роль, обход слепоты) — пишется
/// вручную. Привязка к фигуранту — только на стадии эксперта: верификатор слеп (ТФ-ВЕР-02).
/// </remarks>
public sealed record RecordVerificationCommand(
    int CandidateId,
    VerificationStage Stage,
    VerificationVerdict Verdict,
    string Rationale,
    int? PersonRef = null)
    : IRequest<ResponseDto<CandidateStatus>>
{
    /// <inheritdoc cref="RecordVerificationCommand" />
    public sealed class Handler(
        ISubjectProvider subjectProvider,
        IVerificationPolicy policy,
        IAccessContextProvider accessProvider,
        ISearchSessionStore store,
        ICaseScope caseScope,
        IAuditWriter auditWriter)
        : IRequestHandler<RecordVerificationCommand, ResponseDto<CandidateStatus>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<CandidateStatus>> Handle(
            RecordVerificationCommand command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);

            // «Кто» обязателен (ТБ-072: решение без субъекта — не решение).
            var userId = await subjectProvider.GetCurrentUserIdAsync(cancellationToken);
            if (userId is not { } subjectId)
            {
                return ResponseDto<CandidateStatus>.BadRequest("Субъект не установлен.");
            }

            // Fail-closed (ТБ-021): без контекста допуска — исключение, ни одной записи не делается.
            var access = await accessProvider.GetCurrentAsync(cancellationToken);

            // ТП-004: стадия — только соответствующей роли; попытка чужой роли фиксируется (ТБ-073).
            if (!await policy.CanActAsync(command.Stage, subjectId, cancellationToken))
            {
                await AuditDeniedAsync(command, subjectId, access.MaxClassification, divisionId: null,
                    "роль не даёт права решения на этой стадии (ТП-004)", cancellationToken);
                return ResponseDto<CandidateStatus>.BadRequest("Решение доступно только ролям Эксперт/Верификатор.");
            }

            var candidate = await store.GetCandidateAsync(command.CandidateId, access, cancellationToken);
            if (candidate is null)
            {
                return ResponseDto<CandidateStatus>.NotFound("Кандидат не найден или недоступен.");
            }

            // Слепота верификатора (ТФ-ВЕР-02): фигуранта привязывает только эксперт — попытка обхода фиксируется.
            if (command.Stage == VerificationStage.Verifier && command.PersonRef is not null)
            {
                await AuditDeniedAsync(command, subjectId, candidate.Classification, candidate.DivisionId,
                    "верификатор попытался привязать фигуранта (ТФ-ВЕР-02)", cancellationToken);
                return ResponseDto<CandidateStatus>.BadRequest("Привязка к фигуранту выполняется только на стадии эксперта (ТФ-ВЕР-02).");
            }

            // ПРАВИЛО ДВУХ ЛИЦ (ТБ-073, GATE-5): самоподтверждение, повтор стадии, верификация до эксперта —
            // отклоняются И аудируются; профиль это правило ослабить не может.
            if (!TwoPersonRule.CanDecide(candidate.Decisions, command.Stage, subjectId, out var reason))
            {
                await AuditDeniedAsync(command, subjectId, candidate.Classification, candidate.DivisionId,
                    reason ?? "правило двух лиц", cancellationToken);
                return ResponseDto<CandidateStatus>.BadRequest(reason ?? "Решение на этой стадии сейчас невозможно (ТБ-073).");
            }

            var decision = new VerificationDecision(subjectId, command.Stage, command.Verdict, command.Rationale, DateTime.UtcNow);
            var decisions = new List<VerificationDecision>(candidate.Decisions) { decision };
            var newStatus = TwoPersonRule.Resolve(decisions);
            var personRef = command.Stage == VerificationStage.Expert ? command.PersonRef : null;

            try
            {
                await store.RecordDecisionAsync(candidate.Id, decision, newStatus, personRef, cancellationToken);
            }
            catch (InvalidOperationException exception)
            {
                // Гонка: решение этой стадии уже записал другой сотрудник (уникальный индекс хранилища) —
                // попытка фиксируется как отклонённая, статус не трогаем (ТБ-073).
                await AuditDeniedAsync(command, subjectId, candidate.Classification, candidate.DivisionId,
                    exception.Message, cancellationToken);
                return ResponseDto<CandidateStatus>.Conflict(exception.Message);
            }

            var expert = decisions.LastOrDefault(d => d.Stage == VerificationStage.Expert);
            await auditWriter.WriteAsync(
                new AuditEntry(
                    AuditAction.Modify,
                    candidate.Classification,
                    subjectId,
                    ObjectRef: $"media:candidate:{candidate.Id}:decision:{command.Stage}",
                    DivisionId: candidate.DivisionId,
                    PayloadSensitive: BuildPayload(command, subjectId, newStatus, expert)),
                cancellationToken);

            // ТФ-ВЕР-03 → ТФ-ПЕР-02: подтверждённый кандидат становится «появлением» фигуранта в деле.
            if (newStatus == CandidateStatus.Confirmed && expert is not null && (personRef ?? candidate.PersonRef) is { } person)
            {
                await caseScope.RecordAppearanceAsync(
                    new ConfirmedAppearance(
                        candidate.CaseId, person, candidate.SessionId, candidate.Id, candidate.FaceId, candidate.AssetId,
                        candidate.FrameIndex, candidate.FrameTimestampMs, candidate.Similarity,
                        candidate.Classification, candidate.DivisionId,
                        ExpertUserId: expert.SubjectId, VerifierUserId: subjectId, ConfirmedAtUtc: decision.DecidedAtUtc),
                    cancellationToken);
            }

            return ResponseDto<CandidateStatus>.Ok(newStatus);
        }

        private static string BuildPayload(
            RecordVerificationCommand command, int subjectId, CandidateStatus newStatus, VerificationDecision? expert)
        {
            var text = $"стадия={command.Stage}; вердикт={command.Verdict}; статус={newStatus}; обоснование={command.Rationale}";
            if (command.Stage == VerificationStage.Expert && command.PersonRef is { } personRef)
            {
                text += $"; фигурант={personRef}";
            }

            if (newStatus == CandidateStatus.Confirmed && expert is not null)
            {
                // ТБ-072: оба субъекта; ТБ-073: маркировка — версия, а не «установлен».
                text += $"; эксперт={expert.SubjectId}; верификатор={subjectId}; {TwoPersonRule.ConfirmedMarker}";
            }

            return text;
        }

        /// <summary>Отклонённая попытка решения — аудируется (ТБ-073: «попытки нарушить — отклоняются и аудируются»).</summary>
        private Task AuditDeniedAsync(
            RecordVerificationCommand command, int subjectId, short classification, int? divisionId, string reason,
            CancellationToken cancellationToken) =>
            auditWriter.WriteAsync(
                new AuditEntry(
                    AuditAction.Modify,
                    classification,
                    subjectId,
                    ObjectRef: $"media:candidate:{command.CandidateId}:decision-denied",
                    DivisionId: divisionId,
                    PayloadSensitive: $"стадия={command.Stage}; вердикт={command.Verdict}; отклонено: {reason}"),
                cancellationToken);
    }
}
