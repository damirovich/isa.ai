using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Investigation.Domain.Enums;
using ISC.AI.Profile.Investigation.Domain.Services;

namespace ISC.AI.Profile.Investigation.Application.Features.RelationGraph;

/// <summary>Итог поиска цепочки между узлами графа связей (ADR-0045 п. 12).</summary>
/// <param name="Path">Цепочка от начала до цели; <see langword="null"/> — не найдена.</param>
/// <param name="LimitReached">Поиск остановлен предохранителем числа раскрытий — цепочка может существовать дальше.</param>
internal sealed record RelationPathResult(RelationGraph? Path, bool LimitReached);

/// <summary>
/// Обход графа связей (ADR-0045 п. 2–4, 10–12): узлы и их соседи читаются из хранилищ профиля под решёткой субъекта.
/// Экземпляр — на один запрос: кэширует прочитанные дела и фигурантов дел, чтобы не читать их на каждом узле.
/// </summary>
/// <remarks>
/// ИНВАРИАНТ (ТБ-020/021, ТФ-ДЕЛ-03, ТБ-084): соседи узла — ТОЛЬКО то, что субъект видит на карточках. Фигуранты и
/// дела — по полной решётке и роли; чужое дело — лишь строкой пересечения (<see cref="IIntersectionStore"/>) и не
/// раскрывается; автотранспорт и адрес раскрываются только в доступных фигурантов (<see cref="IPersonStore.ListByPlateAsync"/>,
/// <see cref="IPersonStore.ListByAddressAsync"/>); совместные появления — только доступных фигурантов
/// (<see cref="IPersonStore.ListCoAppearancesAsync"/>).
/// </remarks>
internal sealed class RelationGraphExplorer(
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

    private readonly Dictionary<int, PersonRow> _persons = [];
    private readonly Dictionary<int, CaseDetails?> _cases = [];
    private readonly Dictionary<int, IReadOnlyList<PersonRow>> _caseMembers = [];

    /// <summary>
    /// Узел-центр по виду и идентификатору; <see langword="null"/> — недоступен или не существует (ТБ-020/021).
    /// У автотранспорта и адреса <paramref name="id"/> — фигурант, у которого этот реквизит, а <paramref name="key"/>
    /// — ключ узла: реквизит должен у этого доступного фигуранта действительно быть (ADR-0045 п. 10) — так
    /// раскрытием нельзя «прощупать» произвольный госномер или адрес.
    /// </summary>
    public async Task<RelationNode?> ResolveAsync(RelationNodeKind kind, int id, string? key)
    {
        switch (kind)
        {
            case RelationNodeKind.Person:
                return await persons.GetAsync(id, access, cancellationToken) is { } row ? await PersonNodeAsync(row, 0, null) : null;

            case RelationNodeKind.Case:
                return await GetCaseAsync(id) is { } details ? CaseNode(details, 0, null) : null;

            case RelationNodeKind.Vehicle when RelationNodeKeys.ValueOf(kind, key) is { } plate:
            {
                if (await persons.GetAsync(id, access, cancellationToken) is not { } owner
                    || await requisites.GetAsync(id, access, cancellationToken) is not { } own)
                {
                    return null;
                }

                var vehicle = own.Vehicles.FirstOrDefault(v => RequisiteNormalizer.Plate(v.PlateNumber) == plate);
                return vehicle is null ? null : VehicleNode(vehicle, owner, 0, null);
            }

            case RelationNodeKind.Address when RelationNodeKeys.ValueOf(kind, key) is { } address:
            {
                if (await persons.GetAsync(id, access, cancellationToken) is not { } owner
                    || await requisites.GetAsync(id, access, cancellationToken) is not { } own)
                {
                    return null;
                }

                if (own.Addresses.FirstOrDefault(a => RequisiteNormalizer.Address(a.Text) == address) is { } addressRow)
                {
                    return AddressNode(addressRow.Text, address, addressRow.Id, owner, 0, null);
                }

                return RequisiteNormalizer.Address(owner.Questionnaire?.Residence) == address
                    ? AddressNode(owner.Questionnaire!.Residence!, address, 0, owner, 0, null)
                    : null;
            }

            default:
                return null; // чужое дело, реквизит без ключа — не раскрываются (ADR-0045 п. 3)
        }
    }

    /// <summary>Граф вширь от центра (ADR-0045 п. 4): узел раскрывается целиком или остаётся свёрнутым.</summary>
    public async Task<RelationGraph> BuildAsync(RelationNode center, int depth, int maxNodes)
    {
        var nodes = new List<RelationNode> { center };
        var index = new Dictionary<string, int>(StringComparer.Ordinal) { [center.Key] = 0 };
        var edges = new List<RelationEdge>();
        var edgeKeys = new HashSet<string>(StringComparer.Ordinal);
        var truncated = false;
        var frontier = new List<string> { center.Key };

        for (var level = 0; level < depth && frontier.Count > 0; level++)
        {
            var next = new List<string>();
            foreach (var key in frontier)
            {
                var node = nodes[index[key]];
                var (neighbors, found) = await NeighborsAsync(node, level + 1);
                var fresh = neighbors
                    .Where(candidate => !index.ContainsKey(candidate.Key))
                    .DistinctBy(candidate => candidate.Key)
                    .ToList();

                // Предохранитель (п. 4): «раскрыт» всегда значит «все соседи показаны». Центр раскрывается всегда.
                if (level > 0 && nodes.Count + fresh.Count > maxNodes)
                {
                    truncated = true;
                    continue;
                }

                foreach (var candidate in fresh)
                {
                    index[candidate.Key] = nodes.Count;
                    nodes.Add(candidate);
                    if (candidate.Expandable)
                    {
                        next.Add(candidate.Key);
                    }
                }

                foreach (var edge in found)
                {
                    if (!string.Equals(edge.FromKey, edge.ToKey, StringComparison.Ordinal) && edgeKeys.Add(edge.Key))
                    {
                        edges.Add(edge);
                    }
                }

                nodes[index[key]] = node with { Expanded = true };
            }

            frontier = next;
        }

        return new RelationGraph(nodes, edges, truncated);
    }

    /// <summary>
    /// Цепочка от <paramref name="from"/> до узла <paramref name="targetKey"/> (ADR-0045 п. 12): обход вширь по тем же
    /// правилам раскрытия, не дальше <paramref name="maxSteps"/> рёбер и не больше <paramref name="maxExpansions"/>
    /// раскрытий. Соседство считается по всем найденным рёбрам: ребро «реквизит → дело пересечения» приходит при
    /// раскрытии фигуранта, но ведёт от реквизита.
    /// </summary>
    public async Task<RelationPathResult> FindPathAsync(RelationNode from, string targetKey, int maxSteps, int maxExpansions)
    {
        var known = new Dictionary<string, RelationNode>(StringComparer.Ordinal) { [from.Key] = from };
        var adjacency = new Dictionary<string, List<RelationEdge>>(StringComparer.Ordinal);
        var distance = new Dictionary<string, int>(StringComparer.Ordinal) { [from.Key] = 0 };
        var via = new Dictionary<string, RelationEdge>(StringComparer.Ordinal);
        var queue = new Queue<string>();
        queue.Enqueue(from.Key);
        var expansions = 0;

        while (queue.Count > 0)
        {
            var key = queue.Dequeue();
            if (string.Equals(key, targetKey, StringComparison.Ordinal))
            {
                return new RelationPathResult(Reconstruct(targetKey, from.Key, known, via), LimitReached: false);
            }

            var step = distance[key];
            if (step >= maxSteps)
            {
                continue;
            }

            var node = known[key];
            if (node.Expandable)
            {
                if (++expansions > maxExpansions)
                {
                    return new RelationPathResult(null, LimitReached: true);
                }

                var (neighbors, found) = await NeighborsAsync(node, step + 1);
                foreach (var neighbor in neighbors)
                {
                    known.TryAdd(neighbor.Key, neighbor);
                }

                foreach (var edge in found)
                {
                    Adjacent(adjacency, edge.FromKey).Add(edge);
                    Adjacent(adjacency, edge.ToKey).Add(edge);
                }
            }

            // Чужое дело цепочку не продолжает (ADR-0045 п. 12): два пересечения с одним чужим делом — не связь людей.
            if (node.Kind == RelationNodeKind.ForeignCase)
            {
                continue;
            }

            foreach (var edge in adjacency.GetValueOrDefault(key) ?? [])
            {
                var other = string.Equals(edge.FromKey, key, StringComparison.Ordinal) ? edge.ToKey : edge.FromKey;
                if (known.ContainsKey(other) && distance.TryAdd(other, step + 1))
                {
                    via[other] = edge;
                    queue.Enqueue(other);
                }
            }
        }

        return new RelationPathResult(null, LimitReached: false);
    }

    /// <summary>Узел фигуранта: роль, псевдоним и номер дела во второй строке.</summary>
    public async Task<RelationNode> PersonNodeAsync(PersonRow row, int level, string? parentKey)
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
            role + alias + inCase, level, parentKey, Expanded: false, Classification: row.Classification);
    }

    private async Task<(List<RelationNode> Nodes, List<RelationEdge> Edges)> NeighborsAsync(RelationNode node, int level)
    {
        switch (node.Kind)
        {
            case RelationNodeKind.Person when node.EntityId is { } personId:
                if (!_persons.TryGetValue(personId, out var row))
                {
                    if (await persons.GetAsync(personId, access, cancellationToken) is not { } loaded)
                    {
                        return ([], []);
                    }

                    row = loaded;
                    _persons[personId] = row;
                }

                return await PersonNeighborsAsync(row, node.Key, level);

            case RelationNodeKind.Case when node.EntityId is { } caseId:
                return await CaseNeighborsAsync(caseId, node.Key, level);

            case RelationNodeKind.Vehicle when node.Expandable && RelationNodeKeys.ValueOf(node.Kind, node.Key) is { } plate:
                return await VehicleNeighborsAsync(plate, node.Key, level);

            case RelationNodeKind.Address when node.Expandable && RelationNodeKeys.ValueOf(node.Kind, node.Key) is { } address:
                return await AddressNeighborsAsync(address, node.Key, level);

            default:
                return ([], []);
        }
    }

    // Фигурант → дело, связи из того же дела (в обе стороны), автотранспорт, адреса, пересечения, совместные появления.
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
                var vehicleNode = VehicleNode(vehicle, row, level, key);
                nodes.Add(vehicleNode);
                edges.Add(new RelationEdge(key, vehicleNode.Key, RelationEdgeKind.Requisite, "автотранспорт"));
            }

            foreach (var address in own.Addresses)
            {
                var addressNode = AddressNode(address.Text, RequisiteNormalizer.Address(address.Text), address.Id, row, level, key);
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
            var residence = AddressNode(row.Questionnaire!.Residence!, residenceKey, 0, row, level, key);
            nodes.Add(residence);
            ownAddressKeys.Add(residence.Key);
            edges.Add(new RelationEdge(key, residence.Key, RelationEdgeKind.Requisite, ResidenceLabel));
        }

        // Пересечения (ТФ-ПЕР-07): строки уже отфильтрованы хранилищем по ТБ-084; отклонённые человеком не рисуются.
        foreach (var match in await intersections.FindForPersonAsync(row.Id, access, cancellationToken) ?? [])
        {
            if (match.Decision == IntersectionDecision.Rejected)
            {
                continue;
            }

            // Дело, которое субъект видит и по роли, — обычный узел дела (с грифом и ведущим из карточки дела).
            var target = match.CanOpenCase && await GetCaseAsync(match.OtherCaseId) is { } other
                ? CaseNode(other, level, key)
                : ForeignCaseNode(match, level, key);
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

        // Совместные появления (ADR-0045 п. 11): оба фигуранта доступны, появления действующие и под floor'ом.
        foreach (var together in await persons.ListCoAppearancesAsync(row.Id, access, cancellationToken))
        {
            var otherNode = await PersonNodeAsync(together.Person, level, key);
            nodes.Add(otherNode);
            edges.Add(new RelationEdge(key, otherNode.Key, RelationEdgeKind.CoAppearance, $"вместе на материалах: {together.MaterialCount}"));
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

    // Автотранспорт → доступные фигуранты с тем же госномером (ADR-0045 п. 10); чужие дела — только пересечением.
    private async Task<(List<RelationNode> Nodes, List<RelationEdge> Edges)> VehicleNeighborsAsync(string plate, string key, int level)
    {
        var nodes = new List<RelationNode>();
        var edges = new List<RelationEdge>();
        foreach (var owner in await persons.ListByPlateAsync(plate, access, cancellationToken))
        {
            var personNode = await PersonNodeAsync(owner, level, key);
            nodes.Add(personNode);
            edges.Add(new RelationEdge(personNode.Key, key, RelationEdgeKind.Requisite, "автотранспорт"));
        }

        return (nodes, edges);
    }

    // Адрес → доступные фигуранты с тем же адресом строкой или местом жительства (ADR-0045 п. 10).
    private async Task<(List<RelationNode> Nodes, List<RelationEdge> Edges)> AddressNeighborsAsync(string address, string key, int level)
    {
        var nodes = new List<RelationNode>();
        var edges = new List<RelationEdge>();
        foreach (var match in await persons.ListByAddressAsync(address, access, cancellationToken))
        {
            var personNode = await PersonNodeAsync(match.Person, level, key);
            nodes.Add(personNode);
            edges.Add(new RelationEdge(personNode.Key, key, RelationEdgeKind.Requisite,
                match.Kind is { } kind ? kind.Label().ToLowerInvariant() : ResidenceLabel));
        }

        return (nodes, edges);
    }

    private const string ResidenceLabel = "жительство (анкета)";

    private static RelationNode CaseNode(CaseDetails details, int level, string? parentKey) =>
        new(RelationNodeKeys.Case(details.Id), RelationNodeKind.Case, details.Id, $"Дело № {details.Number}",
            $"{details.Kind.Label()} · {details.Status.Label()}", level, parentKey, Expanded: false, details.InvestigatorUserId,
            details.Classification);

    // Чужое дело — ровно поля пересечения (ТБ-084, ADR-0029 п. 4); ни идентификатора, ни грифа: ни ссылки, ни раскрытия.
    private static RelationNode ForeignCaseNode(IntersectionRow match, int level, string parentKey) =>
        new(RelationNodeKeys.ForeignCase(match.OtherCaseId), RelationNodeKind.ForeignCase, null, $"Дело № {match.OtherCaseNumber}",
            $"{match.OtherCaseKind.Label()} · {match.OtherCaseStatus.Label()}", level, parentKey, Expanded: false, match.ResponsibleUserId);

    // Автотранспорт: узел на нормализованный госномер (раскрывается); без номера — узел на строку (не раскрывается).
    // Фигурант-владелец — для проверки раскрытия на сервере (ResolveAsync) и для грифа узла.
    private static RelationNode VehicleNode(PersonVehicleRow vehicle, PersonRow owner, int level, string? parentKey)
    {
        var details = string.Join(' ', new[] { vehicle.Make, vehicle.Model, vehicle.Color }
            .Where(part => !string.IsNullOrWhiteSpace(part))
            .Select(part => part!.Trim()));
        if (RequisiteNormalizer.Plate(vehicle.PlateNumber) is { Length: > 0 } plate)
        {
            return new RelationNode(RelationNodeKeys.Vehicle(plate), RelationNodeKind.Vehicle, owner.Id, vehicle.PlateNumber!.Trim(),
                details.Length == 0 ? null : details, level, parentKey, Expanded: false, Classification: owner.Classification);
        }

        return new RelationNode(RelationNodeKeys.VehicleRow(vehicle.Id), RelationNodeKind.Vehicle, owner.Id,
            details.Length == 0 ? "Автотранспорт" : details, "без госномера", level, parentKey, Expanded: false,
            Classification: owner.Classification)
        {
            Expandable = false,
        };
    }

    private static RelationNode AddressNode(string text, string? normalized, int addressId, PersonRow owner, int level, string? parentKey) =>
        normalized is { Length: > 0 }
            ? new RelationNode(RelationNodeKeys.Address(normalized), RelationNodeKind.Address, owner.Id, text.Trim(), null, level, parentKey,
                Expanded: false, Classification: owner.Classification)
            : new RelationNode(RelationNodeKeys.AddressRow(addressId), RelationNodeKind.Address, owner.Id, text.Trim(), null, level, parentKey,
                Expanded: false, Classification: owner.Classification)
            {
                Expandable = false,
            };

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

    private static List<RelationEdge> Adjacent(Dictionary<string, List<RelationEdge>> adjacency, string key)
    {
        if (!adjacency.TryGetValue(key, out var list))
        {
            list = [];
            adjacency[key] = list;
        }

        return list;
    }

    // Цепочка по обратным ссылкам: узлы от начала к цели, кольцо = номер шага, родитель — предыдущий узел.
    private static RelationGraph Reconstruct(
        string targetKey, string fromKey, Dictionary<string, RelationNode> known, Dictionary<string, RelationEdge> via)
    {
        var keys = new List<string> { targetKey };
        var edges = new List<RelationEdge>();
        var current = targetKey;
        while (!string.Equals(current, fromKey, StringComparison.Ordinal))
        {
            var edge = via[current];
            edges.Add(edge);
            current = string.Equals(edge.FromKey, current, StringComparison.Ordinal) ? edge.ToKey : edge.FromKey;
            keys.Add(current);
        }

        keys.Reverse();
        edges.Reverse();
        var nodes = keys
            .Select((key, step) => known[key] with { Level = step, ParentKey = step == 0 ? null : keys[step - 1], Expanded = false })
            .ToList();
        return new RelationGraph(nodes, edges, Truncated: false);
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
            foreach (var member in members)
            {
                _persons[member.Id] = member;
            }
        }

        return members;
    }
}
