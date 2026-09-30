using ISC.AI.Profile.Investigation.Domain.Services;

namespace ISC.AI.Profile.Investigation.UI;

/// <summary>Строка дерева подразделений для показа.</summary>
/// <param name="Node">Подразделение.</param>
/// <param name="Depth">Уровень вложенности (0 — верхний).</param>
/// <param name="HasChildren">Есть дочерние (с учётом отбора).</param>
/// <param name="Expanded">Ветка раскрыта.</param>
/// <param name="Path">Путь «Родитель / … / Узел» — для подсказок и выбора родителя.</param>
public sealed record DivisionTreeRow(DivisionNode Node, int Depth, bool HasChildren, bool Expanded, string Path);

/// <summary>
/// Дерево справочника подразделений любой глубины (ТФ-АДМ-01) из плоского списка: порядок «родитель, под ним дочерние»,
/// свёрнутые ветки, поиск (находится узел — видна и его цепочка родителей), сироты (родитель выключен или удалён
/// вручную) — на верхнем уровне, чтобы не потерялись; циклы в данных не зацикливают показ.
/// </summary>
public static class DivisionTree
{
    /// <summary>Строки дерева.</summary>
    /// <param name="nodes">Все подразделения.</param>
    /// <param name="collapsed">Свёрнутые ветки (идентификаторы узлов).</param>
    /// <param name="search">Текст поиска по наименованию и коду; при поиске ветки раскрыты.</param>
    /// <param name="showInactive">Показывать выключенные.</param>
    public static IReadOnlyList<DivisionTreeRow> Build(
        IReadOnlyCollection<DivisionNode> nodes, IReadOnlySet<int> collapsed, string? search, bool showInactive)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(collapsed);

        var byId = nodes.ToDictionary(n => n.Id);
        var children = nodes
            .Where(n => n.ParentId is { } p && byId.ContainsKey(p) && p != n.Id)
            .ToLookup(n => n.ParentId!.Value);
        var roots = nodes.Where(n => n.ParentId is not { } p || !byId.ContainsKey(p) || p == n.Id);

        // Видимые узлы: отбор по состоянию и тексту; у найденного видны все предки.
        var text = search?.Trim();
        var visible = new HashSet<int>();
        foreach (var node in nodes)
        {
            if ((!showInactive && !node.IsActive)
                || (!string.IsNullOrEmpty(text) && !Matches(node, text)))
            {
                continue;
            }

            var current = node;
            var guard = 0;
            while (current is not null && visible.Add(current.Id) && guard++ < nodes.Count)
            {
                current = current.ParentId is { } p && byId.TryGetValue(p, out var parent) ? parent : null;
            }
        }

        var searching = !string.IsNullOrEmpty(text);
        var rows = new List<DivisionTreeRow>();
        var seen = new HashSet<int>();
        void Walk(DivisionNode node, int depth, string path)
        {
            if (!visible.Contains(node.Id) || !seen.Add(node.Id))
            {
                return;
            }

            var kids = children[node.Id].Where(c => visible.Contains(c.Id)).OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            var expanded = searching || !collapsed.Contains(node.Id);
            var fullPath = string.IsNullOrEmpty(path) ? node.Name : $"{path} / {node.Name}";
            rows.Add(new DivisionTreeRow(node, depth, kids.Count > 0, expanded, fullPath));
            if (expanded)
            {
                foreach (var kid in kids)
                {
                    Walk(kid, depth + 1, fullPath);
                }
            }
        }

        foreach (var root in roots.OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            Walk(root, 0, string.Empty);
        }

        // Узлы, до которых обход не дошёл (цикл «А в Б, Б в А» в данных), — в конце, с верхнего уровня: не теряются.
        var reachable = new HashSet<int>();
        var stack = new Stack<DivisionNode>(roots);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            if (reachable.Add(node.Id))
            {
                foreach (var kid in children[node.Id])
                {
                    stack.Push(kid);
                }
            }
        }

        foreach (var rest in nodes.Where(n => visible.Contains(n.Id) && !reachable.Contains(n.Id) && !seen.Contains(n.Id))
                     .OrderBy(n => n.Name, StringComparer.CurrentCultureIgnoreCase).ToList())
        {
            Walk(rest, 0, string.Empty);
        }

        return rows;
    }

    private static bool Matches(DivisionNode node, string text) =>
        node.Name.Contains(text, StringComparison.CurrentCultureIgnoreCase)
        || (node.Code?.Contains(text, StringComparison.CurrentCultureIgnoreCase) ?? false);
}
