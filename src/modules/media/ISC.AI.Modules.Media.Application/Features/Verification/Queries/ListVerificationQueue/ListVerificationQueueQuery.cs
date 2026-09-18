using System;
using System.Collections.Generic;
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
/// Очередь стадии верификации (ТФ-ВЕР-01/02): кандидаты, ждущие решения субъекта на этой стадии, по делам,
/// доступным ему. Выдача — слепая проекция <see cref="VerificationQueueItem"/> (без чужих решений и фигуранта).
/// </summary>
public sealed record ListVerificationQueueQuery(VerificationStage Stage)
    : IRequest<ResponseDto<IReadOnlyList<VerificationQueueItem>>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.View;

    /// <inheritdoc />
    public string? AuditSummary => $"media:verification:queue:{Stage}";

    /// <inheritdoc cref="ListVerificationQueueQuery" />
    public sealed class Handler(
        ISubjectProvider subjectProvider,
        IVerificationPolicy policy,
        IAccessContextProvider accessProvider,
        ICaseScope caseScope,
        ISearchSessionStore store)
        : IRequestHandler<ListVerificationQueueQuery, ResponseDto<IReadOnlyList<VerificationQueueItem>>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<IReadOnlyList<VerificationQueueItem>>> Handle(
            ListVerificationQueueQuery query, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(query);

            var userId = await subjectProvider.GetCurrentUserIdAsync(cancellationToken);
            if (userId is not { } subjectId)
            {
                return ResponseDto<IReadOnlyList<VerificationQueueItem>>.BadRequest("Субъект не установлен.");
            }

            // ТП-004: стадия доступна только соответствующей роли (эксперт по лицам / верификатор).
            if (!await policy.CanActAsync(query.Stage, subjectId, cancellationToken))
            {
                return ResponseDto<IReadOnlyList<VerificationQueueItem>>.BadRequest("Очередь доступна только ролям Эксперт/Верификатор.");
            }

            // Fail-closed (ТБ-020/021): решётка — на стороне БД; область — дела субъекта (ТБ-071).
            var access = await accessProvider.GetCurrentAsync(cancellationToken);
            var cases = await caseScope.ListAccessibleCasesAsync(access, cancellationToken);
            var caseIds = new List<int>(cases.Count);
            foreach (var item in cases)
            {
                caseIds.Add(item.CaseId);
            }

            if (caseIds.Count == 0)
            {
                return ResponseDto<IReadOnlyList<VerificationQueueItem>>.Ok([], 0);
            }

            var candidates = await store.ListQueueAsync(query.Stage, caseIds, access, cancellationToken);

            // Проба (вырезка/хеш) — из сессии; сессии кэшируем: в очереди много кандидатов одной сессии.
            var sessions = new Dictionary<int, SearchSessionRow?>();
            var items = new List<VerificationQueueItem>(candidates.Count);
            foreach (var candidate in candidates)
            {
                if (!sessions.TryGetValue(candidate.SessionId, out var session))
                {
                    session = await store.GetAsync(candidate.SessionId, access, cancellationToken);
                    sessions[candidate.SessionId] = session;
                }

                if (session is null)
                {
                    continue; // сессия вне допуска — кандидат не показывается (fail-closed)
                }

                items.Add(VerificationQueueItem.From(candidate, session, subjectId));
            }

            return ResponseDto<IReadOnlyList<VerificationQueueItem>>.Ok(items, items.Count);
        }
    }
}
