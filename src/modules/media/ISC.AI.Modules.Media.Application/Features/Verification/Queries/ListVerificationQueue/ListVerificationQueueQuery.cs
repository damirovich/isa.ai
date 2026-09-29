using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentValidation;
using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.Media.Domain.Model;
using ISC.AI.Modules.Media.Domain.Services;
using Mediator;

namespace ISC.AI.Modules.Media.Application.Features.Verification;

/// <summary>
/// Очередь стадии верификации (ТФ-ВЕР-01/02): кандидаты, ждущие решения субъекта на этой стадии, по делам,
/// доступным ему. Выдача — слепая проекция <see cref="VerificationQueueItem"/> (без чужих решений и фигуранта),
/// ПОСТРАНИЧНО: в ответе строки страницы <paramref name="Page"/>, а в <c>TotalCount</c> — сколько всего
/// кандидатов в очереди стадии в пределах допуска.
/// </summary>
/// <param name="Stage">Стадия верификации.</param>
/// <param name="Page">Номер страницы, с 1.</param>
/// <param name="PageSize">Размер страницы, 1..<see cref="MaxPageSize"/>.</param>
public sealed record ListVerificationQueueQuery(VerificationStage Stage, int Page = 1, int PageSize = ListVerificationQueueQuery.DefaultPageSize)
    : IRequest<ResponseDto<IReadOnlyList<VerificationQueueItem>>>, IAuditableRequest
{
    /// <summary>Размер страницы по умолчанию.</summary>
    public const int DefaultPageSize = 24;

    /// <summary>Наибольший размер страницы: очередь целиком за один запрос не отдаётся.</summary>
    public const int MaxPageSize = 100;

    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.View;

    /// <inheritdoc />
    public string? AuditSummary => $"media:verification:queue:{Stage}:page:{Page}";

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

            // Страница — на стороне БД; границы зажимаются и здесь (валидатор — первый рубеж, этот — последний).
            var pageSize = Math.Clamp(query.PageSize, 1, MaxPageSize);
            var skip = (long)(Math.Max(query.Page, 1) - 1) * pageSize;
            var page = await store.ListQueuePageAsync(
                query.Stage, caseIds, (int)Math.Min(skip, int.MaxValue), pageSize, access, cancellationToken);

            // Проба (вырезка/хеш) — из сессии; сессии кэшируем: на странице много кандидатов одной сессии.
            var sessions = new Dictionary<int, SearchSessionRow?>();
            var items = new List<VerificationQueueItem>(page.Rows.Count);
            foreach (var candidate in page.Rows)
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

            return ResponseDto<IReadOnlyList<VerificationQueueItem>>.Ok(items, page.Total);
        }
    }
}

/// <summary>Границы страницы очереди верификации.</summary>
public sealed class ListVerificationQueueValidator : AbstractValidator<ListVerificationQueueQuery>
{
    /// <inheritdoc cref="ListVerificationQueueValidator" />
    public ListVerificationQueueValidator()
    {
        RuleFor(q => q.Stage).IsInEnum();
        RuleFor(q => q.Page).GreaterThanOrEqualTo(1);
        RuleFor(q => q.PageSize).InclusiveBetween(1, ListVerificationQueueQuery.MaxPageSize);
    }
}
