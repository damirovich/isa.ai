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

namespace ISC.AI.Modules.Media.Application.Features.Search;

/// <summary>Поисковая сессия с кандидат-листом (ТФ-ПЛ-02/07): для инициатора поиска и руководителя.</summary>
/// <param name="Session">Сессия.</param>
/// <param name="Candidates">Кандидаты по рангу — с решениями (ПОЛНАЯ строка; очередь верификатора получает проекцию).</param>
public sealed record SearchSessionDetails(SearchSessionRow Session, IReadOnlyList<SearchCandidateRow> Candidates);

/// <summary>Сессия поиска по идентификатору (ТФ-ПЛ-07). Просмотр кандидат-листа аудируется (ТБ-030).</summary>
public sealed record GetSearchSessionQuery(int SessionId) : IRequest<ResponseDto<SearchSessionDetails>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.View;

    /// <inheritdoc />
    public string? AuditSummary => $"media:search:{SessionId}:view";

    /// <inheritdoc cref="GetSearchSessionQuery" />
    public sealed class Handler(IAccessContextProvider accessProvider, ISearchSessionStore store)
        : IRequestHandler<GetSearchSessionQuery, ResponseDto<SearchSessionDetails>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<SearchSessionDetails>> Handle(
            GetSearchSessionQuery query, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(query);

            // Fail-closed (ТБ-020/021): сессия под решёткой дела; недоступная неотличима от несуществующей.
            var access = await accessProvider.GetCurrentAsync(cancellationToken);
            var session = await store.GetAsync(query.SessionId, access, cancellationToken);
            if (session is null)
            {
                return ResponseDto<SearchSessionDetails>.NotFound("Поисковая сессия не найдена или недоступна.");
            }

            var candidates = await store.ListCandidatesAsync(session.Id, access, cancellationToken);
            return ResponseDto<SearchSessionDetails>.Ok(new SearchSessionDetails(session, candidates));
        }
    }
}
