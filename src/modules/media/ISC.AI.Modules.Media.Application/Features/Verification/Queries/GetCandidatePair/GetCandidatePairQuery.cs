using System;
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
/// Карточка пары «проба ↔ кандидат» для решения на стадии (ТФ-ВЕР-01): та же слепая проекция, что и в
/// очереди (ТФ-ВЕР-02) — без чужих решений и без фигуранта. Просмотр аудируется (ТБ-030).
/// </summary>
public sealed record GetCandidatePairQuery(int CandidateId, VerificationStage Stage)
    : IRequest<ResponseDto<VerificationQueueItem>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.View;

    /// <inheritdoc />
    public string? AuditSummary => $"media:candidate:{CandidateId}:pair:{Stage}";

    /// <inheritdoc cref="GetCandidatePairQuery" />
    public sealed class Handler(
        ISubjectProvider subjectProvider,
        IVerificationPolicy policy,
        IAccessContextProvider accessProvider,
        ISearchSessionStore store)
        : IRequestHandler<GetCandidatePairQuery, ResponseDto<VerificationQueueItem>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<VerificationQueueItem>> Handle(
            GetCandidatePairQuery query, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(query);

            var userId = await subjectProvider.GetCurrentUserIdAsync(cancellationToken);
            if (userId is not { } subjectId)
            {
                return ResponseDto<VerificationQueueItem>.BadRequest("Субъект не установлен.");
            }

            if (!await policy.CanActAsync(query.Stage, subjectId, cancellationToken))
            {
                return ResponseDto<VerificationQueueItem>.BadRequest("Карточка пары доступна только ролям Эксперт/Верификатор.");
            }

            // Fail-closed (ТБ-020/021): кандидат и сессия — под решёткой дела.
            var access = await accessProvider.GetCurrentAsync(cancellationToken);
            var candidate = await store.GetCandidateAsync(query.CandidateId, access, cancellationToken);
            if (candidate is null)
            {
                return ResponseDto<VerificationQueueItem>.NotFound("Кандидат не найден или недоступен.");
            }

            var session = await store.GetAsync(candidate.SessionId, access, cancellationToken);
            if (session is null)
            {
                return ResponseDto<VerificationQueueItem>.NotFound("Кандидат не найден или недоступен.");
            }

            return ResponseDto<VerificationQueueItem>.Ok(VerificationQueueItem.From(candidate, session, subjectId));
        }
    }
}
