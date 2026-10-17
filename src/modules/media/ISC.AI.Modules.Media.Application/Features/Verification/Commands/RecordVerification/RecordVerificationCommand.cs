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
/// Записать решение стадии верификации по кандидату (ТФ-ВЕР-01..03, ТФ-ВЕР-07, ТБ-073, GATE-5). Статус кандидата
/// считается ТОЛЬКО правилом подтверждения (<see cref="TwoPersonRule"/>) по решениям людей; никакого
/// автоматического решения нет. В режиме одного оператора (по умолчанию, ADR-0054) «подтверждён» оператора, в режиме
/// двух лиц — два «подтверждён» РАЗНЫХ сотрудников дают «следственную версию — требует процессуальной проверки» и
/// появление фигуранта в деле (ТФ-ПЕР-02). «Неопределённо» оператора или расхождение двоих — итог выносит руководитель,
/// третье лицо (ТФ-ВЕР-02, ADR-0036).
/// </summary>
/// <remarks>
/// НЕ <see cref="IAuditableRequest"/>: запись ТБ-072 требует ОБОИХ субъектов при подтверждении и отдельной
/// фиксации ОТКЛОНЁННЫХ попыток (самоподтверждение, повтор стадии, чужая роль, обход слепоты) — пишется
/// вручную. Привязка к фигуранту — на стадии эксперта и у руководителя: верификатор слеп (ТФ-ВЕР-02).
/// В режиме двух лиц «подтверждён» руководителя даёт статус только вместе с положительным решением эксперта или
/// верификатора (<see cref="TwoPersonRule.CanSupervisorDecide"/>); в появлении первая подпись — это решение, вторая —
/// руководитель. РЕЖИМ РАЗБОРА (ADR-0054): решение эксперта записывается с режимом, действующим сейчас (настройка
/// Администратора), решения верификатора и руководителя — с режимом эксперта; так смена настройки не пересчитывает
/// решённое и не меняет правил начатого разбора. В режиме одного оператора у появления одна подпись — того, кто подтвердил.
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
        IAuditWriter auditWriter,
        IMediaSettingsStore settingsStore)
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
                return ResponseDto<CandidateStatus>.BadRequest(command.Stage == VerificationStage.Supervisor
                    ? VerificationMessages.SupervisorDenied
                    : "Решение доступно только ролям Эксперт/Верификатор.");
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

            // ТБ-073, ADR-0036: руководитель подтверждает только вместе с положительным решением другого сотрудника.
            if (command.Stage == VerificationStage.Supervisor
                && !TwoPersonRule.CanSupervisorDecide(candidate.Decisions, command.Verdict, subjectId, out var supervisorReason))
            {
                await AuditDeniedAsync(command, subjectId, candidate.Classification, candidate.DivisionId,
                    supervisorReason ?? "правило двух лиц", cancellationToken);
                return ResponseDto<CandidateStatus>.BadRequest(supervisorReason ?? "Такой итог руководителю недоступен (ТБ-073).");
            }

            var personRef = command.Stage is VerificationStage.Expert or VerificationStage.Supervisor ? command.PersonRef : null;

            // ТФ-ВЕР-03: «подтверждён» без фигуранта дал бы статус без «появления» — отказ (дублирует валидатор:
            // обработчик вызывается и напрямую, не только через конвейер). Руководитель может оставить фигуранта,
            // которого привязал эксперт.
            if (command.Verdict == VerificationVerdict.Confirmed
                && ((command.Stage == VerificationStage.Expert && personRef is null)
                    || (command.Stage == VerificationStage.Supervisor && (personRef ?? candidate.PersonRef) is null)))
            {
                return ResponseDto<CandidateStatus>.BadRequest(RecordVerificationValidator.PersonRequiredMessage);
            }

            // ТБ-020/070, ТФ-ВЕР-03 «фигурант ДЕЛА»: фигурант — только из дела кандидата, видимого субъекту;
            // чужой/несуществующий идентификатор ушёл бы «появлением» в чужое дело — отказ аудируется.
            if (personRef is { } requestedPerson)
            {
                var persons = await caseScope.ListPersonsAsync(candidate.CaseId, access, cancellationToken);
                if (!persons.Any(p => p.PersonId == requestedPerson))
                {
                    await AuditDeniedAsync(command, subjectId, candidate.Classification, candidate.DivisionId,
                        "фигурант не принадлежит делу кандидата (ТФ-ВЕР-03)", cancellationToken);
                    return ResponseDto<CandidateStatus>.BadRequest("Фигурант не принадлежит делу кандидата либо недоступен (ТФ-ВЕР-03).");
                }
            }

            // ТФ-ВЕР-07: режим разбора задаёт первое решение (эксперта) по действующей настройке; дальше — режим эксперта.
            // Без решения эксперта другие стадии сюда не доходят (CanDecide), запасное «два лица» — наиболее строгое.
            var mode = command.Stage == VerificationStage.Expert
                ? (await settingsStore.GetAsync(cancellationToken)).Mode
                : TwoPersonRule.ModeOf(candidate.Decisions) ?? ConfirmationMode.TwoPerson;
            var decision = new VerificationDecision(
                subjectId, command.Stage, command.Verdict, command.Rationale, DateTime.UtcNow, mode);
            var decisions = new List<VerificationDecision>(candidate.Decisions) { decision };
            var newStatus = TwoPersonRule.Resolve(decisions);

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
            var verifier = decisions.LastOrDefault(d => d.Stage == VerificationStage.Verifier);

            // Подписи под «подтверждён». Один оператор (ТФ-ВЕР-07): одна — того, кто подтвердил (оператор либо руководитель
            // по «неопределённому»). Два лица: первая — эксперт, а при решении руководителя — тот из двоих, кто подтвердил;
            // вторая — текущий субъект (верификатор или руководитель).
            var single = mode == ConfirmationMode.SingleOperator;
            var firstSignature = single ? decision
                : command.Stage == VerificationStage.Supervisor ? TwoPersonRule.PositiveBy(expert, verifier, subjectId)
                : expert;
            int? secondSignature = single ? null : subjectId;
            await auditWriter.WriteAsync(
                new AuditEntry(
                    AuditAction.Modify,
                    candidate.Classification,
                    subjectId,
                    ObjectRef: $"media:candidate:{candidate.Id}:decision:{command.Stage}",
                    DivisionId: candidate.DivisionId,
                    PayloadSensitive: BuildPayload(command, subjectId, newStatus, mode, expert, verifier, firstSignature)),
                cancellationToken);

            // ТФ-ВЕР-03 → ТФ-ПЕР-02: подтверждённый кандидат становится «появлением» фигуранта в деле.
            if (newStatus == CandidateStatus.Confirmed && firstSignature is not null && (personRef ?? candidate.PersonRef) is { } person)
            {
                await caseScope.RecordAppearanceAsync(
                    new ConfirmedAppearance(
                        candidate.CaseId, person, candidate.SessionId, candidate.Id, candidate.FaceId, candidate.AssetId,
                        candidate.FrameIndex, candidate.FrameTimestampMs, candidate.Similarity,
                        candidate.Classification, candidate.DivisionId,
                        ExpertUserId: firstSignature.SubjectId, VerifierUserId: secondSignature, ConfirmedAtUtc: decision.DecidedAtUtc),
                    cancellationToken);
            }

            return ResponseDto<CandidateStatus>.Ok(newStatus);
        }

        private static string BuildPayload(
            RecordVerificationCommand command, int subjectId, CandidateStatus newStatus, ConfirmationMode mode,
            VerificationDecision? expert, VerificationDecision? verifier, VerificationDecision? firstSignature)
        {
            var single = mode == ConfirmationMode.SingleOperator;
            var text = $"стадия={command.Stage}; вердикт={command.Verdict}; статус={newStatus}; "
                + $"режим={(single ? "один оператор" : "два лица")}; обоснование={command.Rationale}";
            if ((command.Stage is VerificationStage.Expert or VerificationStage.Supervisor) && command.PersonRef is { } personRef)
            {
                text += $"; фигурант={personRef}";
            }

            if (command.Stage == VerificationStage.Supervisor)
            {
                // ТФ-ВЕР-02: итог по расхождению — с обоими прежними решениями, чтобы журнал показывал, что было решено.
                text += $"; решение руководителя={subjectId}; эксперт={expert?.SubjectId}:{expert?.Verdict}"
                    + (verifier is null ? string.Empty : $"; верификатор={verifier.SubjectId}:{verifier.Verdict}");
            }

            if (newStatus == CandidateStatus.Confirmed && firstSignature is not null)
            {
                // ТБ-072: все подписавшие; ТБ-073: маркировка — версия, а не «установлен».
                text += single
                    ? $"; подтвердил={firstSignature.SubjectId}; {TwoPersonRule.ConfirmedMarker}"
                    : command.Stage == VerificationStage.Supervisor
                        ? $"; подписи: {firstSignature.SubjectId} и руководитель {subjectId}; {TwoPersonRule.ConfirmedMarker}"
                        : $"; эксперт={firstSignature.SubjectId}; верификатор={subjectId}; {TwoPersonRule.ConfirmedMarker}";
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
