using System;
using System.Globalization;
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
/// «Как связаны» (ТФ-ПЕР-10, ADR-0045 п. 12): цепочка связей от центра графа (фигуранта или дела) до выбранного
/// фигуранта — обход вширь по тем же правилам раскрытия, что у графа, до <see cref="MaxSteps"/> шагов и
/// <see cref="MaxExpansions"/> раскрытых узлов. Ответ — граф из узлов и рёбер цепочки.
/// </summary>
/// <param name="FromKind">Начало: фигурант, дело или событие (центр страницы графа).</param>
/// <param name="FromId">Фигурант или дело.</param>
/// <param name="ToPersonId">Фигурант, до которого ищется цепочка.</param>
/// <remarks>
/// ИНВАРИАНТ (ТБ-020/021, ТБ-084): цепочка идёт только по сведениям, которые субъект видит на карточках
/// (<see cref="RelationGraphExplorer"/>); чужое дело её не продолжает. Недоступное начало или цель — «не найден», как
/// карточка; «цепочка не найдена» говорит только о доступных сведениях и ничего не сообщает о недоступных.
/// </remarks>
public sealed record FindRelationPathQuery(RelationNodeKind FromKind, int FromId, int ToPersonId)
    : IRequest<ResponseDto<RelationGraph>>, IAuditableRequest
{
    /// <summary>Наибольшая длина цепочки, рёбер (как наибольшая глубина графа).</summary>
    public const int MaxSteps = GetRelationGraphQuery.MaxDepth;

    /// <summary>Предохранитель: сколько узлов можно раскрыть в поиске.</summary>
    public const int MaxExpansions = 500;

    /// <summary>Цепочки в пределах доступных субъекту сведений нет.</summary>
    public const string NoPath = "В пределах доступных вам сведений цепочка связей не найдена (до 8 шагов).";

    /// <summary>Поиск остановлен предохранителем.</summary>
    public const string LimitReached =
        "Цепочка не найдена: просмотрено 500 узлов — это предел поиска. Постройте граф от другого фигуранта или дела.";

    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.View;

    /// <inheritdoc />
    /// <remarks>Только виды и идентификаторы (ТБ-032).</remarks>
    public string? AuditSummary => string.Create(
        CultureInfo.InvariantCulture,
        $"investigation:graph:path:{RelationNodeKeys.AuditName(FromKind)}:{FromId}:person:{ToPersonId}:view");

    /// <inheritdoc cref="FindRelationPathQuery" />
    public sealed class Handler(
        IPersonStore persons,
        IPersonRequisiteStore requisites,
        ICaseStore cases,
        IIntersectionStore intersections,
        ILinkEventStore events,
        IReferenceStore references,
        IAccessContextProvider accessProvider)
        : IRequestHandler<FindRelationPathQuery, ResponseDto<RelationGraph>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<RelationGraph>> Handle(FindRelationPathQuery query, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(query);

            var access = await accessProvider.GetCurrentAsync(cancellationToken);
            var explorer = await RelationGraphExplorers.CreateAsync(
                persons, requisites, cases, intersections, events, references, access, cancellationToken);

            // Начало и цель — под полной решёткой: недоступные неотличимы от несуществующих (ТБ-020/021).
            var from = await explorer.ResolveAsync(query.FromKind, query.FromId, key: null);
            var to = await explorer.ResolveAsync(RelationNodeKind.Person, query.ToPersonId, key: null);
            if (from is null || to is null)
            {
                return ResponseDto<RelationGraph>.NotFound(GetRelationGraphQuery.NotFound);
            }

            var result = await explorer.FindPathAsync(from, to.Key, MaxSteps, MaxExpansions);
            if (result.Path is { } path)
            {
                return ResponseDto<RelationGraph>.Ok(path, path.Edges.Count);
            }

            return ResponseDto<RelationGraph>.NotFound(result.LimitReached ? LimitReached : NoPath);
        }
    }
}

/// <summary>Правила запроса цепочки: начало — фигурант или дело, цель — фигурант.</summary>
public sealed class FindRelationPathValidator : AbstractValidator<FindRelationPathQuery>
{
    /// <inheritdoc cref="FindRelationPathValidator" />
    public FindRelationPathValidator()
    {
        RuleFor(q => q.FromKind)
            .Must(kind => kind is RelationNodeKind.Person or RelationNodeKind.Case or RelationNodeKind.Event)
            .WithMessage("Цепочка ищется от фигуранта, дела или события.");
        RuleFor(q => q.FromId).GreaterThan(0);
        RuleFor(q => q.ToPersonId).GreaterThan(0);
    }
}
