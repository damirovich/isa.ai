using System;
using System.Globalization;
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

namespace ISC.AI.Profile.Investigation.Application.Features.RelationGraph;

/// <summary>
/// Граф связей фигуранта или дела (ТФ-ПЕР-10, ADR-0045): обход вширь от центра на <paramref name="Depth"/> колец.
/// Тот же запрос с глубиной 1 раскрывает узел по щелчку на странице графа — в том числе автотранспорт и адрес.
/// </summary>
/// <param name="CenterKind">Вид центра: фигурант, дело; при раскрытии щелчком — ещё автотранспорт и адрес.</param>
/// <param name="CenterId">Фигурант или дело; у автотранспорта и адреса — фигурант, у которого этот реквизит.</param>
/// <param name="Depth">Сколько колец раскрыть: 1–8.</param>
/// <param name="CenterKey">Ключ узла автотранспорта или адреса (<see cref="RelationNodeKeys"/>); у фигуранта и дела — нет.</param>
/// <remarks>
/// ИНВАРИАНТ (ТБ-020/021, ТФ-ДЕЛ-03, ТБ-084): граф собирается ТОЛЬКО из тех же хранилищ, что и карточки, под той же
/// решёткой — новых путей к данным нет (<see cref="RelationGraphExplorer"/>). Недоступный центр неотличим от
/// несуществующего. Чужое дело приходит лишь строкой пересечения и показывается как в пересечениях; фигуранты чужого
/// дела не читаются вовсе. Ключ узла от клиента не доверяется: узел заново читается из хранилища под решёткой, а
/// реквизит должен действительно быть у названного доступного фигуранта (ADR-0045 п. 10).
/// </remarks>
public sealed record GetRelationGraphQuery(
    RelationNodeKind CenterKind, int CenterId, int Depth = GetRelationGraphQuery.DefaultDepth, string? CenterKey = null)
    : IRequest<ResponseDto<RelationGraph>>, IAuditableRequest
{
    /// <summary>Глубина по умолчанию: фигурант → его дело и реквизиты → их соседи.</summary>
    public const int DefaultDepth = 2;

    /// <summary>Наименьшая глубина.</summary>
    public const int MinDepth = 1;

    /// <summary>Наибольшая глубина (как в АСИО-10).</summary>
    public const int MaxDepth = 8;

    /// <summary>
    /// Предохранитель числа узлов (как в АСИО-10): узел, чьи новые соседи не помещаются, остаётся свёрнутым; центр
    /// раскрывается всегда.
    /// </summary>
    public const int MaxNodes = 300;

    /// <summary>Предел длины ключа узла реквизита (нормализованный адрес — до 1000 знаков, ТО-мат-11).</summary>
    public const int MaxKeyLength = 1100;

    /// <summary>Неразличимый ответ «нет такого фигуранта/дела либо недоступны» (ТБ-020/021).</summary>
    public const string NotFound = "Фигурант или дело не найдены либо недоступны.";

    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.View;

    /// <inheritdoc />
    /// <remarks>
    /// Только вид, идентификатор и глубина — без ФИО, госномеров и адресов (ТБ-032); у автотранспорта и адреса —
    /// фигурант, через которого раскрыт реквизит, а не его значение.
    /// </remarks>
    public string? AuditSummary => CenterKind is RelationNodeKind.Vehicle or RelationNodeKind.Address
        ? string.Create(CultureInfo.InvariantCulture,
            $"investigation:graph:{RelationNodeKeys.AuditName(CenterKind)}:person:{CenterId}:view;depth={Depth}")
        : string.Create(CultureInfo.InvariantCulture,
            $"investigation:graph:{RelationNodeKeys.AuditName(CenterKind)}:{CenterId}:view;depth={Depth}");

    /// <inheritdoc cref="GetRelationGraphQuery" />
    public sealed class Handler(
        IPersonStore persons,
        IPersonRequisiteStore requisites,
        ICaseStore cases,
        IIntersectionStore intersections,
        IReferenceStore references,
        IAccessContextProvider accessProvider)
        : IRequestHandler<GetRelationGraphQuery, ResponseDto<RelationGraph>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<RelationGraph>> Handle(GetRelationGraphQuery query, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(query);

            // Fail-closed (ТБ-021): без контекста допуска провайдер бросает.
            var access = await accessProvider.GetCurrentAsync(cancellationToken);
            var explorer = await RelationGraphExplorers.CreateAsync(
                persons, requisites, cases, intersections, references, access, cancellationToken);

            var center = await explorer.ResolveAsync(query.CenterKind, query.CenterId, query.CenterKey);
            if (center is null)
            {
                return ResponseDto<RelationGraph>.NotFound(NotFound);
            }

            var graph = await explorer.BuildAsync(center, Math.Clamp(query.Depth, MinDepth, MaxDepth), MaxNodes);
            return ResponseDto<RelationGraph>.Ok(graph, graph.Nodes.Count);
        }
    }
}

/// <summary>
/// Правила запроса графа: центр — фигурант, дело, автотранспорт или адрес (у двух последних — с ключом узла),
/// глубина 1–8.
/// </summary>
public sealed class GetRelationGraphValidator : AbstractValidator<GetRelationGraphQuery>
{
    /// <inheritdoc cref="GetRelationGraphValidator" />
    public GetRelationGraphValidator()
    {
        RuleFor(q => q.CenterKind)
            .Must(RelationNodeKeys.IsExpandable)
            .WithMessage("Граф строится от фигуранта, дела, автотранспорта или адреса.");
        RuleFor(q => q.CenterId).GreaterThan(0);
        RuleFor(q => q.Depth).InclusiveBetween(GetRelationGraphQuery.MinDepth, GetRelationGraphQuery.MaxDepth);
        RuleFor(q => q.CenterKey)
            .Must((query, key) => query.CenterKind is RelationNodeKind.Vehicle or RelationNodeKind.Address
                ? RelationNodeKeys.ValueOf(query.CenterKind, key) is not null && key!.Length <= GetRelationGraphQuery.MaxKeyLength
                : key is null)
            .WithMessage("Ключ узла указывается только для автотранспорта и адреса.");
    }
}

/// <summary>Создание обхода графа на запрос: справочник типов связей читается один раз.</summary>
internal static class RelationGraphExplorers
{
    /// <summary>Обход под допуском субъекта.</summary>
    public static async Task<RelationGraphExplorer> CreateAsync(
        IPersonStore persons,
        IPersonRequisiteStore requisites,
        ICaseStore cases,
        IIntersectionStore intersections,
        IReferenceStore references,
        AccessContext access,
        CancellationToken cancellationToken)
    {
        // Справочник «тип связи» — подписи рёбер связей (ТФ-ПЕР-06); выключенные записи тоже: связь с ними уже есть.
        var linkTypes = (await references.ListAsync(ReferenceKind.LinkType, cancellationToken))
            .ToDictionary(item => item.Id, item => item.Name);
        return new RelationGraphExplorer(persons, requisites, cases, intersections, linkTypes, access, cancellationToken);
    }
}
