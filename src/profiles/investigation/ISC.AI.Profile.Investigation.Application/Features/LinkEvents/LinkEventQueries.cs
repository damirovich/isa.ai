using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentValidation;
using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Investigation.Domain.Enums;
using ISC.AI.Profile.Investigation.Domain.Services;
using Mediator;

namespace ISC.AI.Profile.Investigation.Application.Features.LinkEvents;

/// <summary>
/// События фигуранта — «дела-посредники» его связей с объектами других дел (ТФ-ПЕР-11, ADR-0056): видимые субъекту
/// события и их видимые участники. Ответ «не найден» — фигурант недоступен (ТБ-021).
/// </summary>
/// <param name="PersonId">Фигурант.</param>
public sealed record ListPersonEventsQuery(int PersonId) : IRequest<ResponseDto<IReadOnlyList<LinkEventRow>>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.View;

    /// <inheritdoc />
    public string? AuditSummary => $"investigation:person:{PersonId}:events:view";

    /// <inheritdoc cref="ListPersonEventsQuery" />
    public sealed class Handler(ILinkEventStore store, IAccessContextProvider accessProvider)
        : IRequestHandler<ListPersonEventsQuery, ResponseDto<IReadOnlyList<LinkEventRow>>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<IReadOnlyList<LinkEventRow>>> Handle(ListPersonEventsQuery query, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(query);

            var access = await accessProvider.GetCurrentAsync(cancellationToken);
            return await store.ListForPersonAsync(query.PersonId, access, cancellationToken) is { } events
                ? ResponseDto<IReadOnlyList<LinkEventRow>>.Ok(events, events.Count)
                : ResponseDto<IReadOnlyList<LinkEventRow>>.NotFound(LinkEventGuard.NotFound);
        }
    }
}

/// <summary>Правила запроса событий фигуранта.</summary>
public sealed class ListPersonEventsValidator : AbstractValidator<ListPersonEventsQuery>
{
    /// <inheritdoc cref="ListPersonEventsValidator" />
    public ListPersonEventsValidator()
    {
        RuleFor(q => q.PersonId).GreaterThan(0);
    }
}

/// <summary>Фигурант, которого можно выбрать вторым объектом связи: ФИО и подпись «роль · «псевдоним» · дело № …».</summary>
/// <param name="PersonId">Фигурант.</param>
/// <param name="DisplayName">ФИО.</param>
/// <param name="Details">Роль, псевдоним, номер дела.</param>
public sealed record EventLinkCandidate(int PersonId, string DisplayName, string Details);

/// <summary>
/// Выбор второго объекта связи через событие (ТФ-ПЕР-11): поиск по ФИО или псевдониму среди фигурантов, доступных
/// субъекту по полной решётке и роли, — объект вне допуска для связи не выбирается. Не больше <see cref="MaxResults"/>.
/// </summary>
/// <param name="Text">Часть ФИО или псевдонима.</param>
/// <remarks>В журнал — факт поиска без текста запроса (ТБ-032: ФИО в сводку не пишется).</remarks>
public sealed record SearchEventLinkCandidatesQuery(string Text)
    : IRequest<ResponseDto<IReadOnlyList<EventLinkCandidate>>>, IAuditableRequest
{
    /// <summary>Наибольшее число строк ответа.</summary>
    public const int MaxResults = 20;

    /// <summary>Предел длины запроса.</summary>
    public const int MaxTextLength = 200;

    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.Search;

    /// <inheritdoc />
    public string? AuditSummary => "investigation:event:person-search";

    /// <inheritdoc cref="SearchEventLinkCandidatesQuery" />
    public sealed class Handler(IPersonStore persons, ICaseStore cases, IAccessContextProvider accessProvider)
        : IRequestHandler<SearchEventLinkCandidatesQuery, ResponseDto<IReadOnlyList<EventLinkCandidate>>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<IReadOnlyList<EventLinkCandidate>>> Handle(
            SearchEventLinkCandidatesQuery query, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(query);

            var access = await accessProvider.GetCurrentAsync(cancellationToken);
            var found = await persons.SearchAsync(query.Text, MaxResults, access, cancellationToken);

            // Номер дела — подпись, по которой сотрудник отличает тёзок; дело доступно, раз доступен фигурант.
            var numbers = new Dictionary<int, string?>();
            foreach (var caseId in found.Select(p => p.CaseId).Distinct())
            {
                numbers[caseId] = (await cases.GetAsync(caseId, access, cancellationToken))?.Number;
            }

            var hits = found
                .Select(p => new EventLinkCandidate(p.Id, p.DisplayName, Details(p, numbers.GetValueOrDefault(p.CaseId))))
                .ToList();
            return ResponseDto<IReadOnlyList<EventLinkCandidate>>.Ok(hits, hits.Count);
        }

        private static string Details(PersonRow row, string? caseNumber)
        {
            var parts = new List<string> { row.Role.Label() };
            if (!string.IsNullOrWhiteSpace(row.Questionnaire?.Alias))
            {
                parts.Add($"«{row.Questionnaire!.Alias!.Trim()}»");
            }

            if (caseNumber is not null)
            {
                parts.Add($"дело № {caseNumber}");
            }

            return string.Join(" · ", parts);
        }
    }
}

/// <summary>Правила выбора второго объекта: текст задан и не длиннее предела.</summary>
public sealed class SearchEventLinkCandidatesValidator : AbstractValidator<SearchEventLinkCandidatesQuery>
{
    /// <inheritdoc cref="SearchEventLinkCandidatesValidator" />
    public SearchEventLinkCandidatesValidator()
    {
        RuleFor(q => q.Text).NotNull().MaximumLength(SearchEventLinkCandidatesQuery.MaxTextLength);
    }
}
