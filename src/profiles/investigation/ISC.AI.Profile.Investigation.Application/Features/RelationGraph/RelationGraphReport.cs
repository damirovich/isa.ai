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
using ISC.AI.Modules.DocFlow.Domain.Services;
using ISC.AI.Profile.Investigation.Application.Features.Statistics;
using ISC.AI.Profile.Investigation.Domain.Services;
using Mediator;

namespace ISC.AI.Profile.Investigation.Application.Features.RelationGraph;

/// <summary>Объект отчёта по графу: фигурант, его порядок и то, через что он связан с исходным.</summary>
/// <param name="Node">Узел фигуранта.</param>
/// <param name="Order">Порядок: 1 — напрямую, 2 и 3 — через одного и двух других фигурантов.</param>
/// <param name="Via">Для 1-го порядка — через что связь; для 2-го и 3-го — цепочка от исходного объекта.</param>
public sealed record GraphReportPerson(RelationNode Node, int Order, string Via);

/// <summary>Пересечение по реквизиту: автотранспорт, адрес или телефон, общий у нескольких фигурантов или дел.</summary>
/// <param name="Node">Узел реквизита.</param>
/// <param name="Shared">У кого совпадает: фигуранты и дела через запятую.</param>
public sealed record GraphReportIntersection(RelationNode Node, string Shared);

/// <summary>Отчёт по графу связей (ТФ-ОТЧ-06, [АИС 6.4], ADR-0061 ч. 4).</summary>
/// <param name="Center">Исходный объект.</param>
/// <param name="Cases">Его дела и карточки.</param>
/// <param name="Persons">Объекты 1-го порядка и цепочки 2-го и 3-го порядка.</param>
/// <param name="Intersections">Пересечения по транспорту, адресам и телефонам.</param>
/// <param name="Truncated">Сработал предохранитель числа узлов графа — часть связей не раскрыта.</param>
/// <param name="MaxClassification">Гриф отчёта — наибольший гриф узлов, вошедших в отчёт (ТБ-033).</param>
public sealed record RelationGraphReport(
    RelationNode Center,
    IReadOnlyList<RelationNode> Cases,
    IReadOnlyList<GraphReportPerson> Persons,
    IReadOnlyList<GraphReportIntersection> Intersections,
    bool Truncated,
    short MaxClassification);

/// <summary>
/// Разбор графа связей в отчёт (ADR-0061 ч. 4): порядок фигуранта — число фигурантов на кратчайшем пути от исходного;
/// промежуточные дела, события и реквизиты порядок не увеличивают.
/// </summary>
public static class RelationGraphReportBuilder
{
    /// <summary>Заголовок (он же основа имени файла).</summary>
    public const string Title = "Отчёт по графу связей";

    /// <summary>Глубина обхода: хватает на цепочки 3-го порядка через дело или реквизит на каждом шаге.</summary>
    public const int Depth = 6;

    /// <summary>Наибольший порядок, который попадает в отчёт ([АИС 6.4]).</summary>
    public const int MaxOrder = 3;

    /// <summary>Колонки — общие для всех разделов.</summary>
    public static IReadOnlyList<string> Columns { get; } = ["Объект", "Сведения", "Связь или цепочка"];

    /// <summary>Отчёт по готовому графу от фигуранта <paramref name="centerKey"/>.</summary>
    public static RelationGraphReport Build(RelationGraph graph, string centerKey)
    {
        ArgumentNullException.ThrowIfNull(graph);
        var nodes = graph.Nodes.ToDictionary(n => n.Key, StringComparer.Ordinal);
        var center = nodes[centerKey];

        var adjacency = new Dictionary<string, List<(string To, RelationEdge Edge)>>(StringComparer.Ordinal);
        foreach (var edge in graph.Edges.Where(e => nodes.ContainsKey(e.FromKey) && nodes.ContainsKey(e.ToKey)))
        {
            Add(adjacency, edge.FromKey, (edge.ToKey, edge));
            Add(adjacency, edge.ToKey, (edge.FromKey, edge));
        }

        // Обход 0-1: вход в узел фигуранта стоит 1, в любой другой — 0. Кратчайший путь по числу фигурантов.
        var order = new Dictionary<string, int>(StringComparer.Ordinal) { [centerKey] = 0 };
        var parent = new Dictionary<string, (string From, RelationEdge Edge)>(StringComparer.Ordinal);
        var deque = new LinkedList<string>();
        deque.AddFirst(centerKey);
        while (deque.First is { } first)
        {
            deque.RemoveFirst();
            var key = first.Value;
            foreach (var (to, edge) in adjacency.GetValueOrDefault(key) ?? [])
            {
                var cost = nodes[to].Kind == RelationNodeKind.Person ? 1 : 0;
                var candidate = order[key] + cost;
                if (order.TryGetValue(to, out var known) && known <= candidate)
                {
                    continue;
                }

                order[to] = candidate;
                parent[to] = (key, edge);
                if (cost == 0)
                {
                    deque.AddFirst(to);
                }
                else
                {
                    deque.AddLast(to);
                }
            }
        }

        var included = new HashSet<string>(StringComparer.Ordinal) { centerKey };

        var cases = (adjacency.GetValueOrDefault(centerKey) ?? [])
            .Select(n => nodes[n.To])
            .Where(n => n.Kind is RelationNodeKind.Case or RelationNodeKind.ForeignCase)
            .DistinctBy(n => n.Key)
            .OrderBy(n => n.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        included.UnionWith(cases.Select(n => n.Key));

        var persons = new List<GraphReportPerson>();
        foreach (var (key, value) in order.Where(o => o.Value is >= 1 and <= MaxOrder && nodes[o.Key].Kind == RelationNodeKind.Person))
        {
            var path = PathTo(key, centerKey, parent);
            included.UnionWith(path.Select(step => step.Key));
            var via = value == 1
                ? Via(path, nodes)
                : string.Join(" → ", path.Select(step => Step(step, nodes)).Prepend(center.Title));
            persons.Add(new GraphReportPerson(nodes[key], value, via));
        }

        persons = persons.OrderBy(p => p.Order).ThenBy(p => p.Node.Title, StringComparer.CurrentCultureIgnoreCase).ToList();

        var intersections = new List<GraphReportIntersection>();
        foreach (var node in graph.Nodes.Where(n => n.Kind is RelationNodeKind.Vehicle or RelationNodeKind.Address or RelationNodeKind.Phone))
        {
            var shared = (adjacency.GetValueOrDefault(node.Key) ?? [])
                .Select(n => nodes[n.To])
                .Where(n => n.Kind is RelationNodeKind.Person or RelationNodeKind.Case or RelationNodeKind.ForeignCase)
                .DistinctBy(n => n.Key)
                .ToList();
            if (shared.Count < 2)
            {
                continue;
            }

            included.Add(node.Key);
            included.UnionWith(shared.Select(n => n.Key));
            intersections.Add(new GraphReportIntersection(
                node, string.Join(", ", shared.OrderBy(n => n.Title, StringComparer.CurrentCultureIgnoreCase).Select(n => n.Title))));
        }

        intersections = intersections.OrderBy(i => i.Node.Kind).ThenBy(i => i.Node.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
        var max = included.Select(k => nodes[k].Classification ?? ClassificationLevels.Unclassified).DefaultIfEmpty((short)0).Max();
        return new RelationGraphReport(center, cases, persons, intersections, graph.Truncated, ClassificationLevels.Normalize(max));
    }

    /// <summary>Представление для выгрузки: разделы [АИС 6.4] одной таблицей с общими колонками.</summary>
    public static ReportView ToView(RelationGraphReport report, string generatedBy, DateTime generatedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(report);
        var filter = new List<string>
        {
            "Исходный объект: " + report.Center.Title,
            $"Объекты до {MaxOrder.ToString(CultureInfo.InvariantCulture)}-го порядка; дела и события, через которые связь, — промежуточные звенья",
        };
        if (report.Truncated)
        {
            filter.Add("Граф большой: часть связей не раскрыта предохранителем — постройте граф от нужного объекта отдельно.");
        }

        var first = report.Persons.Where(p => p.Order == 1).ToList();
        var second = report.Persons.Where(p => p.Order == 2).ToList();
        var third = report.Persons.Where(p => p.Order == 3).ToList();
        return new ReportView(
            Title,
            ClassificationLevels.Marking(report.MaxClassification),
            StatisticsReports.Stamp(generatedBy, generatedAtUtc),
            filter,
            Columns,
            [
                Group("Исходный объект", [Row(report.Center.Title, report.Center.Subtitle, "—")]),
                Group("Дела и карточки", report.Cases.Select(c => Row(c.Title, c.Subtitle, KindLabel(c.Kind))).ToList()),
                Group("Объекты 1-го порядка", first.Select(p => Row(p.Node.Title, p.Node.Subtitle, p.Via)).ToList()),
                Group("Цепочки 2-го порядка", second.Select(p => Row(p.Node.Title, p.Node.Subtitle, p.Via)).ToList()),
                Group("Цепочки 3-го порядка", third.Select(p => Row(p.Node.Title, p.Node.Subtitle, p.Via)).ToList()),
                Group("Пересечения по транспорту, адресам и телефонам",
                    report.Intersections.Select(i => Row(i.Node.Title, KindLabel(i.Node.Kind), i.Shared)).ToList()),
            ],
            [
                new ReportTotal("Дел и карточек", Number(report.Cases.Count)),
                new ReportTotal("Объектов 1-го порядка", Number(first.Count)),
                new ReportTotal("Объектов 2-го порядка", Number(second.Count)),
                new ReportTotal("Объектов 3-го порядка", Number(third.Count)),
                new ReportTotal("Пересечений", Number(report.Intersections.Count)),
            ]);
    }

    /// <summary>Вид узла словами.</summary>
    public static string KindLabel(RelationNodeKind kind) => kind switch
    {
        RelationNodeKind.Person => "фигурант",
        RelationNodeKind.Case => "дело",
        RelationNodeKind.ForeignCase => "дело другого подразделения",
        RelationNodeKind.Vehicle => "автотранспорт",
        RelationNodeKind.Address => "адрес",
        RelationNodeKind.Phone => "телефон",
        RelationNodeKind.Event => "событие",
        _ => kind.ToString(),
    };

    // Путь от исходного к узлу: шаги (узел и ребро, по которому в него пришли), без исходного.
    private static List<(string Key, RelationEdge Edge)> PathTo(
        string key, string centerKey, Dictionary<string, (string From, RelationEdge Edge)> parent)
    {
        var path = new List<(string Key, RelationEdge Edge)>();
        while (!string.Equals(key, centerKey, StringComparison.Ordinal) && parent.TryGetValue(key, out var step))
        {
            path.Add((key, step.Edge));
            key = step.From;
        }

        path.Reverse();
        return path;
    }

    // Через что связь 1-го порядка: промежуточные звенья (дело, событие, реквизит), а при прямой связи — её подпись.
    private static string Via(List<(string Key, RelationEdge Edge)> path, Dictionary<string, RelationNode> nodes)
    {
        var middle = path.Take(path.Count - 1).Select(step => nodes[step.Key]).ToList();
        return middle.Count > 0
            ? string.Join(" → ", middle.Select(n => $"{KindLabel(n.Kind)}: {n.Title}"))
            : path[^1].Edge.Label;
    }

    // Шаг цепочки: узел, а для прямой связи двух фигурантов — ещё и её подпись.
    private static string Step((string Key, RelationEdge Edge) step, Dictionary<string, RelationNode> nodes) =>
        step.Edge.Kind is RelationEdgeKind.PersonLink or RelationEdgeKind.CoAppearance
            ? $"({step.Edge.Label}) {nodes[step.Key].Title}"
            : nodes[step.Key].Title;

    private static ReportViewGroup Group(string title, IReadOnlyList<ReportViewRow> rows) =>
        new(title, rows.Count > 0 ? rows : [Row("нет", null, string.Empty)], []);

    private static ReportViewRow Row(string title, string? details, string link) => new([title, details ?? string.Empty, link]);

    private static void Add(Dictionary<string, List<(string To, RelationEdge Edge)>> adjacency, string from, (string To, RelationEdge Edge) item)
    {
        if (!adjacency.TryGetValue(from, out var list))
        {
            adjacency[from] = list = [];
        }

        list.Add(item);
    }

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// Выгрузить отчёт по графу связей фигуранта (ТФ-ОТЧ-06, [АИС 6.4], ADR-0061 ч. 4) — Word (по ТЗ), Excel или PDF.
/// </summary>
/// <remarks>
/// ИНВАРИАНТ (ТБ-020/021, ТБ-084): граф строится ТЕМ ЖЕ обходом, что страница графа (<see cref="RelationGraphExplorer"/>),
/// под той же решёткой и с тем же предохранителем — новых путей к данным нет; недоступный фигурант неотличим от
/// несуществующего. Гриф — наибольший гриф узлов, вошедших в отчёт; запись <c>Export</c> — до выдачи файла (ТБ-030/033).
/// В журнал — номер фигуранта и формат, без ФИО (ТБ-032). Право — то же, что у графа: кто видит граф, тот и выгружает.
/// </remarks>
/// <param name="PersonId">Исходный фигурант.</param>
/// <param name="Format">Формат файла.</param>
public sealed record ExportRelationGraphReportCommand(int PersonId, ReportFormat Format) : IRequest<ResponseDto<ReportDocument>>
{
    /// <inheritdoc cref="ExportRelationGraphReportCommand" />
    public sealed class Handler(
        IPersonStore persons,
        IPersonRequisiteStore requisites,
        ICaseStore cases,
        IIntersectionStore intersections,
        ILinkEventStore events,
        IReferenceStore references,
        IAccessContextProvider accessProvider,
        IEnumerable<IReportRenderer> renderers,
        ISubjectProvider subjectProvider,
        IUserDirectory users,
        IAuditWriter audit,
        TimeProvider timeProvider)
        : IRequestHandler<ExportRelationGraphReportCommand, ResponseDto<ReportDocument>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<ReportDocument>> Handle(ExportRelationGraphReportCommand command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);

            var access = await accessProvider.GetCurrentAsync(cancellationToken);
            var explorer = await RelationGraphExplorers.CreateAsync(
                persons, requisites, cases, intersections, events, references, access, cancellationToken);
            if (await explorer.ResolveAsync(RelationNodeKind.Person, command.PersonId, null) is not { } center)
            {
                return ResponseDto<ReportDocument>.NotFound(GetRelationGraphQuery.NotFound);
            }

            var graph = await explorer.BuildAsync(center, RelationGraphReportBuilder.Depth, GetRelationGraphQuery.MaxNodes);
            var report = RelationGraphReportBuilder.Build(graph, center.Key);
            return await StatisticsReports.ExportAsync(
                renderers,
                command.Format,
                generatedBy => RelationGraphReportBuilder.ToView(report, generatedBy, timeProvider.GetUtcNow().UtcDateTime),
                report.MaxClassification,
                string.Create(CultureInfo.InvariantCulture, $"investigation:graph:person:{command.PersonId}:report; nodes={graph.Nodes.Count}"),
                subjectProvider,
                users,
                audit,
                cancellationToken);
        }
    }
}

/// <summary>Правила выгрузки отчёта по графу: фигурант — положительный номер, формат — известный.</summary>
public sealed class ExportRelationGraphReportValidator : AbstractValidator<ExportRelationGraphReportCommand>
{
    /// <inheritdoc cref="ExportRelationGraphReportValidator" />
    public ExportRelationGraphReportValidator()
    {
        RuleFor(c => c.PersonId).GreaterThan(0);
        RuleFor(c => c.Format).IsInEnum();
    }
}
