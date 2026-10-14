using System;
using System.Collections.Generic;
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
/// Тот же запрос с глубиной 1 раскрывает узел по щелчку на странице графа.
/// </summary>
/// <param name="CenterKind">Вид центра: фигурант или дело (остальные узлы не раскрываются — отказ валидатора).</param>
/// <param name="CenterId">Фигурант или дело.</param>
/// <param name="Depth">Сколько колец раскрыть: 1–8.</param>
/// <remarks>
/// ИНВАРИАНТ (ТБ-020/021, ТФ-ДЕЛ-03, ТБ-084): граф собирается ТОЛЬКО из тех же хранилищ, что и карточки, под той же
/// решёткой — новых путей к данным нет. Недоступный центр неотличим от несуществующего. Чужое дело приходит лишь
/// строкой пересечения (<see cref="IIntersectionStore"/> — floor без сужения по роли, ADR-0029 п. 1) и показывается
/// как в пересечениях — номер, вид, статус, ведущий; фигуранты чужого дела не читаются вовсе. Ключ узла от клиента не
/// доверяется: раскрытие передаёт вид и идентификатор, и узел заново читается из хранилища под решёткой.
/// </remarks>
public sealed record GetRelationGraphQuery(RelationNodeKind CenterKind, int CenterId, int Depth = GetRelationGraphQuery.DefaultDepth)
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

    /// <summary>Неразличимый ответ «нет такого фигуранта/дела либо недоступны» (ТБ-020/021).</summary>
    public const string NotFound = "Фигурант или дело не найдены либо недоступны.";

    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.View;

    /// <inheritdoc />
    /// <remarks>Только вид, идентификатор и глубина — без ФИО, госномеров и адресов (ТБ-032).</remarks>
    public string? AuditSummary => string.Create(
        CultureInfo.InvariantCulture,
        $"investigation:graph:{(CenterKind == RelationNodeKind.Case ? "case" : "person")}:{CenterId}:view;depth={Depth}");

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

            // Справочник «тип связи» — подписи рёбер связей (ТФ-ПЕР-06); выключенные записи тоже: связь с ними уже есть.
            var linkTypes = (await references.ListAsync(ReferenceKind.LinkType, cancellationToken))
                .ToDictionary(item => item.Id, item => item.Name);

            var builder = new RelationGraphBuilder(persons, requisites, cases, intersections, linkTypes, access, cancellationToken);
            var graph = await builder.BuildAsync(query.CenterKind, query.CenterId, Math.Clamp(query.Depth, MinDepth, MaxDepth), MaxNodes);

            return graph is null
                ? ResponseDto<RelationGraph>.NotFound(NotFound)
                : ResponseDto<RelationGraph>.Ok(graph, graph.Nodes.Count);
        }
    }
}

/// <summary>Правила запроса графа: центр — фигурант или дело, глубина 1–8.</summary>
public sealed class GetRelationGraphValidator : AbstractValidator<GetRelationGraphQuery>
{
    /// <inheritdoc cref="GetRelationGraphValidator" />
    public GetRelationGraphValidator()
    {
        RuleFor(q => q.CenterKind)
            .Must(RelationNodeKeys.IsExpandable)
            .WithMessage("Граф строится от фигуранта или дела.");
        RuleFor(q => q.CenterId).GreaterThan(0);
        RuleFor(q => q.Depth).InclusiveBetween(GetRelationGraphQuery.MinDepth, GetRelationGraphQuery.MaxDepth);
    }
}

/// <summary>
/// Построение графа связей вширь (ADR-0045 п. 2–4). Экземпляр — на один запрос: кэширует прочитанные дела и
/// фигурантов дел, чтобы не читать их повторно на каждом узле.
/// </summary>
internal sealed class RelationGraphBuilder(
    IPersonStore persons,
    IPersonRequisiteStore requisites,
    ICaseStore cases,
    IIntersectionStore intersections,
    IReadOnlyDictionary<int, string> linkTypes,
    AccessContext access,
    CancellationToken cancellationToken)
{
    /// <summary>Предел длины подписи роли в деле (свободный текст) на ребре.</summary>
    private const int MaxLabelLength = 40;

    private readonly List<RelationNode> _nodes = [];
    private readonly Dictionary<string, int> _index = new(StringComparer.Ordinal);
    private readonly List<RelationEdge> _edges = [];
    private readonly HashSet<string> _edgeKeys = new(StringComparer.Ordinal);
    private readonly Dictionary<int, PersonRow> _persons = [];
    private readonly Dictionary<int, CaseDetails?> _cases = [];
    private readonly Dictionary<int, IReadOnlyList<PersonRow>> _caseMembers = [];

    /// <summary>Граф от центра; <see langword="null"/> — центр недоступен или не существует (ТБ-020/021).</summary>
    public async Task<RelationGraph?> BuildAsync(RelationNodeKind centerKind, int centerId, int depth, int maxNodes)
    {
        var center = centerKind switch
        {
            RelationNodeKind.Person => await persons.GetAsync(centerId, access, cancellationToken) is { } row
                ? await PersonNodeAsync(row, 0, null)
                : null,
            RelationNodeKind.Case => await GetCaseAsync(centerId) is { } details
                ? CaseNode(details, 0, null)
                : null,
            _ => null,
        };
        if (center is null)
        {
            return null;
        }

        AddNode(center);
        var truncated = false;
        var frontier = new List<string> { center.Key };

        for (var level = 0; level < depth && frontier.Count > 0; level++)
        {
            var next = new List<string>();
            foreach (var key in frontier)
            {
                var node = _nodes[_index[key]];
                var (neighbors, edges) = await NeighborsAsync(node, level + 1);
                var fresh = neighbors
                    .Where(candidate => !_index.ContainsKey(candidate.Key))
                    .DistinctBy(candidate => candidate.Key)
                    .ToList();

                // Предохранитель (п. 4): узел раскрывается целиком или остаётся свёрнутым — «раскрыт» всегда значит
                // «все соседи показаны». Центр раскрывается всегда.
                if (level > 0 && _nodes.Count + fresh.Count > maxNodes)
                {
                    truncated = true;
                    continue;
                }

                foreach (var candidate in fresh)
                {
                    AddNode(candidate);
                    if (candidate.Expandable)
                    {
                        next.Add(candidate.Key);
                    }
                }

                foreach (var edge in edges)
                {
                    AddEdge(edge);
                }

                _nodes[_index[key]] = node with { Expanded = true };
            }

            frontier = next;
        }

        return new RelationGraph(_nodes, _edges, truncated);
    }

    private async Task<(List<RelationNode> Nodes, List<RelationEdge> Edges)> NeighborsAsync(RelationNode node, int level) =>
        node.Kind switch
        {
            RelationNodeKind.Person when node.EntityId is { } personId && _persons.TryGetValue(personId, out var row) =>
                await PersonNeighborsAsync(row, node.Key, level),
            RelationNodeKind.Case when node.EntityId is { } caseId => await CaseNeighborsAsync(caseId, node.Key, level),
            _ => ([], []),
        };

    // Фигурант → дело, связи из того же дела (в обе стороны), автотранспорт, адреса, пересечения.
    private async Task<(List<RelationNode> Nodes, List<RelationEdge> Edges)> PersonNeighborsAsync(PersonRow row, string key, int level)
    {
        var nodes = new List<RelationNode>();
        var edges = new List<RelationEdge>();

        if (await GetCaseAsync(row.CaseId) is { } details)
        {
            var caseNode = CaseNode(details, level, key);
            nodes.Add(caseNode);
            edges.Add(new RelationEdge(key, caseNode.Key, RelationEdgeKind.CaseMember, MemberLabel(row)));
        }

        // Связи (ТФ-ПЕР-06) — только фигуранты того же дела, которые субъект видит (список дела — под решёткой).
        var members = await GetCaseMembersAsync(row.CaseId);
        if (row.LinkedToPersonId is { } linkedToId && members.FirstOrDefault(m => m.Id == linkedToId) is { } linkedTo)
        {
            var target = await PersonNodeAsync(linkedTo, level, key);
            nodes.Add(target);
            edges.Add(new RelationEdge(key, target.Key, RelationEdgeKind.PersonLink, LinkLabel(row.LinkTypeId)));
        }

        foreach (var link in members.Where(m => m.LinkedToPersonId == row.Id && m.Id != row.Id))
        {
            var source = await PersonNodeAsync(link, level, key);
            nodes.Add(source);
            edges.Add(new RelationEdge(source.Key, key, RelationEdgeKind.PersonLink, LinkLabel(link.LinkTypeId)));
        }

        var ownAddressKeys = new HashSet<string>(StringComparer.Ordinal);
        if (await requisites.GetAsync(row.Id, access, cancellationToken) is { } own)
        {
            foreach (var vehicle in own.Vehicles)
            {
                var vehicleNode = VehicleNode(vehicle, level, key);
                nodes.Add(vehicleNode);
                edges.Add(new RelationEdge(key, vehicleNode.Key, RelationEdgeKind.Requisite, "автотранспорт"));
            }

            foreach (var address in own.Addresses)
            {
                var addressNode = AddressNode(address.Text, RequisiteNormalizer.Address(address.Text), address.Id, level, key);
                nodes.Add(addressNode);
                ownAddressKeys.Add(addressNode.Key);
                edges.Add(new RelationEdge(key, addressNode.Key, RelationEdgeKind.Requisite, address.Kind.Label().ToLowerInvariant()));
            }
        }

        // Место жительства из анкеты — тот же узел адреса, что и строка адреса с тем же ключом (как в пересечениях,
        // ADR-0029 п. 3); если адрес уже есть строкой — второе ребро не рисуется.
        if (RequisiteNormalizer.Address(row.Questionnaire?.Residence) is { Length: > 0 } residenceKey
            && !ownAddressKeys.Contains(RelationNodeKeys.Address(residenceKey)))
        {
            var residence = AddressNode(row.Questionnaire!.Residence!, residenceKey, 0, level, key);
            nodes.Add(residence);
            ownAddressKeys.Add(residence.Key);
            edges.Add(new RelationEdge(key, residence.Key, RelationEdgeKind.Requisite, "жительство (анкета)"));
        }

        // Пересечения (ТФ-ПЕР-07): строки уже отфильтрованы хранилищем по ТБ-084; отклонённые человеком не рисуются.
        foreach (var match in await intersections.FindForPersonAsync(row.Id, access, cancellationToken) ?? [])
        {
            if (match.Decision == IntersectionDecision.Rejected)
            {
                continue;
            }

            var target = match.CanOpenCase ? CaseNode(match, level, key) : ForeignCaseNode(match, level, key);
            nodes.Add(target);

            var from = match.Kind switch
            {
                IntersectionKind.Vehicle => RelationNodeKeys.Vehicle(match.Key),
                IntersectionKind.Address => RelationNodeKeys.Address(match.Key),
                _ => key,
            };
            if (!string.Equals(from, key, StringComparison.Ordinal) && !nodes.Any(n => string.Equals(n.Key, from, StringComparison.Ordinal)))
            {
                from = key; // реквизит не нашёлся среди своих (например, правка между чтениями) — ребро от фигуранта
            }

            edges.Add(new RelationEdge(from, target.Key, RelationEdgeKind.Intersection, IntersectionLabel(match)));
        }

        return (nodes, edges);
    }

    // Дело → его фигуранты (под решёткой: следователь видит фигурантов только своих дел).
    private async Task<(List<RelationNode> Nodes, List<RelationEdge> Edges)> CaseNeighborsAsync(int caseId, string key, int level)
    {
        var nodes = new List<RelationNode>();
        var edges = new List<RelationEdge>();
        foreach (var member in await GetCaseMembersAsync(caseId))
        {
            var personNode = await PersonNodeAsync(member, level, key);
            nodes.Add(personNode);
            edges.Add(new RelationEdge(personNode.Key, key, RelationEdgeKind.CaseMember, MemberLabel(member)));
        }

        return (nodes, edges);
    }

    private async Task<RelationNode> PersonNodeAsync(PersonRow row, int level, string? parentKey)
    {
        _persons[row.Id] = row;
        var caseNumber = (await GetCaseAsync(row.CaseId))?.Number;
        var role = row.Role switch
        {
            PersonRole.Target => "Объект",
            PersonRole.Link => "Связь",
            _ => "Фигурант",
        };
        var alias = string.IsNullOrWhiteSpace(row.Questionnaire?.Alias) ? string.Empty : $" · «{row.Questionnaire!.Alias!.Trim()}»";
        var inCase = caseNumber is null ? string.Empty : $" · дело № {caseNumber}";
        return new RelationNode(RelationNodeKeys.Person(row.Id), RelationNodeKind.Person, row.Id, row.DisplayName,
            role + alias + inCase, level, parentKey, Expanded: false);
    }

    private static RelationNode CaseNode(CaseDetails details, int level, string? parentKey) =>
        new(RelationNodeKeys.Case(details.Id), RelationNodeKind.Case, details.Id, $"Дело № {details.Number}",
            $"{details.Kind.Label()} · {details.Status.Label()}", level, parentKey, Expanded: false, details.InvestigatorUserId);

    // Дело из пересечения, которое субъект видит и по роли (CanOpenCase): обычный узел дела, раскрывается.
    private static RelationNode CaseNode(IntersectionRow match, int level, string parentKey) =>
        new(RelationNodeKeys.Case(match.OtherCaseId), RelationNodeKind.Case, match.OtherCaseId, $"Дело № {match.OtherCaseNumber}",
            $"{match.OtherCaseKind.Label()} · {match.OtherCaseStatus.Label()}", level, parentKey, Expanded: false, match.ResponsibleUserId);

    // Чужое дело — ровно поля пересечения (ТБ-084, ADR-0029 п. 4); идентификатор узлу не отдаётся: ни ссылки, ни раскрытия.
    private static RelationNode ForeignCaseNode(IntersectionRow match, int level, string parentKey) =>
        new(RelationNodeKeys.ForeignCase(match.OtherCaseId), RelationNodeKind.ForeignCase, null, $"Дело № {match.OtherCaseNumber}",
            $"{match.OtherCaseKind.Label()} · {match.OtherCaseStatus.Label()}", level, parentKey, Expanded: false, match.ResponsibleUserId);

    private static RelationNode VehicleNode(PersonVehicleRow vehicle, int level, string parentKey)
    {
        var details = string.Join(' ', new[] { vehicle.Make, vehicle.Model, vehicle.Color }
            .Where(part => !string.IsNullOrWhiteSpace(part))
            .Select(part => part!.Trim()));
        if (RequisiteNormalizer.Plate(vehicle.PlateNumber) is { Length: > 0 } plate)
        {
            return new RelationNode(RelationNodeKeys.Vehicle(plate), RelationNodeKind.Vehicle, null, vehicle.PlateNumber!.Trim(),
                details.Length == 0 ? null : details, level, parentKey, Expanded: false);
        }

        return new RelationNode(RelationNodeKeys.VehicleRow(vehicle.Id), RelationNodeKind.Vehicle, null,
            details.Length == 0 ? "Автотранспорт" : details, "без госномера", level, parentKey, Expanded: false);
    }

    private static RelationNode AddressNode(string text, string? normalized, int addressId, int level, string parentKey) =>
        new(normalized is { Length: > 0 } ? RelationNodeKeys.Address(normalized) : RelationNodeKeys.AddressRow(addressId),
            RelationNodeKind.Address, null, text.Trim(), null, level, parentKey, Expanded: false);

    // Подпись ребра «фигурант — дело»: уточнение роли свободным текстом, иначе роль по перечню.
    private static string MemberLabel(PersonRow row)
    {
        if (!string.IsNullOrWhiteSpace(row.RoleInCase))
        {
            var text = row.RoleInCase.Trim();
            return text.Length <= MaxLabelLength ? text : string.Concat(text.AsSpan(0, MaxLabelLength - 1), "…");
        }

        return row.Role switch
        {
            PersonRole.Target => "объект",
            PersonRole.Link => "связь",
            _ => "фигурант",
        };
    }

    private string LinkLabel(int? linkTypeId) =>
        linkTypeId is { } id && linkTypes.TryGetValue(id, out var name) ? name : "связь";

    private static string IntersectionLabel(IntersectionRow match)
    {
        var what = match.Kind switch
        {
            IntersectionKind.Vehicle => "госномер",
            IntersectionKind.Address => "адрес",
            IntersectionKind.PersonName => "ФИО и дата рождения",
            IntersectionKind.Face => "лицо",
            _ => match.Kind.ToString(),
        };
        return match.Decision == IntersectionDecision.Confirmed
            ? $"пересечение: {what} · подтверждено"
            : $"пересечение: {what}";
    }

    private async Task<CaseDetails?> GetCaseAsync(int caseId)
    {
        if (!_cases.TryGetValue(caseId, out var details))
        {
            details = await cases.GetAsync(caseId, access, cancellationToken);
            _cases[caseId] = details;
        }

        return details;
    }

    private async Task<IReadOnlyList<PersonRow>> GetCaseMembersAsync(int caseId)
    {
        if (!_caseMembers.TryGetValue(caseId, out var members))
        {
            members = await persons.ListByCaseAsync(caseId, access, cancellationToken);
            _caseMembers[caseId] = members;
        }

        return members;
    }

    private void AddNode(RelationNode node)
    {
        _index[node.Key] = _nodes.Count;
        _nodes.Add(node);
    }

    private void AddEdge(RelationEdge edge)
    {
        if (!string.Equals(edge.FromKey, edge.ToKey, StringComparison.Ordinal) && _edgeKeys.Add(edge.Key))
        {
            _edges.Add(edge);
        }
    }
}
