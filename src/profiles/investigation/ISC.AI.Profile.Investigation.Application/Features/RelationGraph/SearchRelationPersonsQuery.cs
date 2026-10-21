using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentValidation;
using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Investigation.Domain.Services;
using Mediator;

namespace ISC.AI.Profile.Investigation.Application.Features.RelationGraph;

/// <summary>
/// Выбор фигуранта для цепочки «как связаны» (ADR-0045 п. 12): поиск по ФИО или псевдониму среди доступных субъекту
/// фигурантов — подстрока без учёта регистра и «ё/е», не короче 2 знаков, не больше <see cref="MaxResults"/> строк.
/// Строка — узел фигуранта графа (ФИО, роль, псевдоним, номер дела).
/// </summary>
/// <param name="Text">Часть ФИО или псевдонима.</param>
/// <remarks>
/// ИНВАРИАНТ (ТБ-020/021): только фигуранты, доступные по полной решётке и роли (<see cref="IPersonStore.SearchAsync"/>).
/// В журнал — факт поиска без текста запроса (ТБ-032: ФИО в сводку не пишется).
/// </remarks>
public sealed record SearchRelationPersonsQuery(string Text)
    : IRequest<ResponseDto<IReadOnlyList<RelationNode>>>, IAuditableRequest
{
    /// <summary>Наибольшее число строк ответа.</summary>
    public const int MaxResults = 20;

    /// <summary>Предел длины запроса.</summary>
    public const int MaxTextLength = 200;

    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.Search;

    /// <inheritdoc />
    public string? AuditSummary => "investigation:graph:person-search";

    /// <inheritdoc cref="SearchRelationPersonsQuery" />
    public sealed class Handler(
        IPersonStore persons,
        IPersonRequisiteStore requisites,
        ICaseStore cases,
        IIntersectionStore intersections,
        ILinkEventStore events,
        IReferenceStore references,
        IAccessContextProvider accessProvider)
        : IRequestHandler<SearchRelationPersonsQuery, ResponseDto<IReadOnlyList<RelationNode>>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<IReadOnlyList<RelationNode>>> Handle(
            SearchRelationPersonsQuery query, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(query);

            var access = await accessProvider.GetCurrentAsync(cancellationToken);
            var found = await persons.SearchAsync(query.Text, MaxResults, access, cancellationToken);
            if (found.Count == 0)
            {
                return ResponseDto<IReadOnlyList<RelationNode>>.Ok([], 0);
            }

            // Подписи — те же, что у узлов графа (роль, псевдоним, номер дела): сотрудник узнаёт человека так же.
            var explorer = await RelationGraphExplorers.CreateAsync(
                persons, requisites, cases, intersections, events, references, access, cancellationToken);
            var hits = new List<RelationNode>(found.Count);
            foreach (var row in found)
            {
                hits.Add(await explorer.PersonNodeAsync(row, 0, null));
            }

            return ResponseDto<IReadOnlyList<RelationNode>>.Ok(hits, hits.Count);
        }
    }
}

/// <summary>Правила поиска фигуранта: текст задан и не длиннее <see cref="SearchRelationPersonsQuery.MaxTextLength"/>.</summary>
public sealed class SearchRelationPersonsValidator : AbstractValidator<SearchRelationPersonsQuery>
{
    /// <inheritdoc cref="SearchRelationPersonsValidator" />
    public SearchRelationPersonsValidator()
    {
        RuleFor(q => q.Text).NotNull().MaximumLength(SearchRelationPersonsQuery.MaxTextLength);
    }
}
