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

/// <summary>Что видит руководитель по кандидату (ТФ-ВЕР-02, ADR-0036): решения сотрудников и привязанного экспертом фигуранта.</summary>
/// <param name="CandidateId">Кандидат.</param>
/// <param name="Decisions">Решения эксперта, верификатора и (если уже есть) руководителя — по стадиям.</param>
/// <param name="LinkedPersonRef">Фигурант, привязанный к кандидату (экспертом или руководителем).</param>
public sealed record SupervisorReview(int CandidateId, IReadOnlyList<VerificationDecision> Decisions, int? LinkedPersonRef);

/// <summary>
/// Решения сотрудников по кандидату — ТОЛЬКО для стадии руководителя (ТФ-ВЕР-02, ADR-0036) и ТОЛЬКО когда разбор
/// закончен: в режиме двух лиц — оба решения записаны, в режиме одного оператора (ADR-0054) — решение оператора. Отдельный запрос, а не поле общей проекции очереди: проекция для эксперта и верификатора остаётся
/// структурно слепой (<see cref="VerificationQueueItem"/>).
/// </summary>
/// <remarks>
/// ПОЧЕМУ «ОБА РЕШЕНИЯ УЖЕ ЕСТЬ»: у одного сотрудника может быть и право руководителя, и право верификатора (по
/// умолчанию — Администратор). Если бы обзор отдавался раньше, он увидел бы решение эксперта, а затем вынес бы своё
/// «слепое» — слепота второй проверки (ТФ-ВЕР-02) была бы обойдена. В режиме одного оператора второй проверки нет —
/// обзор доступен после его решения. Отказ неотличим от «не найден» (ТБ-020/021).
/// </remarks>
/// <param name="CandidateId">Кандидат.</param>
public sealed record GetSupervisorReviewQuery(int CandidateId) : IRequest<ResponseDto<SupervisorReview>>, IAuditableRequest
{
    /// <summary>Единый ответ «нет или нельзя» (ТБ-020/021).</summary>
    public const string NotFoundMessage = "Кандидат не найден или его разбор ещё не закончен.";

    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.View;

    /// <inheritdoc />
    public string? AuditSummary => FormattableString.Invariant($"media:candidate:{CandidateId}:supervisor-review");

    /// <inheritdoc cref="GetSupervisorReviewQuery" />
    public sealed class Handler(
        ISubjectProvider subjectProvider,
        IVerificationPolicy policy,
        IAccessContextProvider accessProvider,
        ISearchSessionStore store)
        : IRequestHandler<GetSupervisorReviewQuery, ResponseDto<SupervisorReview>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<SupervisorReview>> Handle(GetSupervisorReviewQuery query, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(query);

            var userId = await subjectProvider.GetCurrentUserIdAsync(cancellationToken);
            if (userId is not { } subjectId)
            {
                return ResponseDto<SupervisorReview>.BadRequest("Субъект не установлен.");
            }

            if (!await policy.CanActAsync(VerificationStage.Supervisor, subjectId, cancellationToken))
            {
                return ResponseDto<SupervisorReview>.BadRequest(VerificationMessages.SupervisorDenied);
            }

            // Fail-closed (ТБ-020/021): кандидат — под решёткой; до конца разбора обзора нет (см. remarks).
            var access = await accessProvider.GetCurrentAsync(cancellationToken);
            var candidate = await store.GetCandidateAsync(query.CandidateId, access, cancellationToken);
            if (candidate is null
                || TwoPersonRule.ModeOf(candidate.Decisions) is not { } mode
                || (mode != ConfirmationMode.SingleOperator
                    && !candidate.Decisions.Any(d => d.Stage == VerificationStage.Verifier)))
            {
                return ResponseDto<SupervisorReview>.NotFound(NotFoundMessage);
            }

            var decisions = candidate.Decisions.OrderBy(d => d.Stage).ThenBy(d => d.DecidedAtUtc).ToList();
            return ResponseDto<SupervisorReview>.Ok(new SupervisorReview(candidate.Id, decisions, candidate.PersonRef));
        }
    }
}
