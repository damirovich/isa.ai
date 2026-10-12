using System;
using System.Collections.Generic;
using System.Linq;
using ISC.AI.Modules.Admin.Application.Features.Clearances;

namespace ISC.AI.Modules.Admin.UI;

/// <summary>Строка дерева подразделений на экране допуска.</summary>
/// <param name="Division">Подразделение справочника профиля.</param>
/// <param name="Depth">Уровень вложенности (0 — верхний).</param>
/// <param name="DescendantIds">Все вложенные подразделения на любой глубине — для «+ вложенные».</param>
public sealed record ClearanceTreeRow(ClearanceDivision Division, int Depth, IReadOnlyList<int> DescendantIds)
{
    /// <summary>Подпись строки: собственное наименование (вышестоящее видно по отступу).</summary>
    public string Title => Division.OwnName ?? Division.Name ?? $"№ {Division.Id}";

    /// <summary>Есть ли вложенные подразделения.</summary>
    public bool HasChildren => DescendantIds.Count > 0;
}

/// <summary>
/// Дерево подразделений для выдачи допуска (ТБ-011/020): «родитель, под ним вложенные» с отступом вместо склеенных
/// названий «Управление / Отдел». Допуск — это ровно отмеченные номера: отметка у управления НЕ открывает его
/// отделы, поэтому дерево ещё и знает всех вложенных каждой строки — для кнопки «+ вложенные».
/// </summary>
/// <remarks>
/// Порядок внутри уровня — по наименованию. Подразделение, чьего родителя нет в справочнике, показывается на
/// верхнем уровне, чтобы не потеряться. Цикл в данных («А в Б, Б в А») не зацикливает построение: такие строки
/// выводятся в конце, с верхнего уровня.
/// </remarks>
public static class ClearanceDivisionTree
{
    /// <summary>Строки дерева по справочнику профиля.</summary>
    public static IReadOnlyList<ClearanceTreeRow> Build(IReadOnlyCollection<ClearanceDivision> divisions)
    {
        ArgumentNullException.ThrowIfNull(divisions);

        var byId = new Dictionary<int, ClearanceDivision>();
        foreach (var division in divisions)
        {
            byId[division.Id] = division;
        }

        var children = byId.Values
            .Where(d => d.ParentId is { } parent && parent != d.Id && byId.ContainsKey(parent))
            .GroupBy(d => d.ParentId!.Value)
            .ToDictionary(g => g.Key, g => g.OrderBy(Title, StringComparer.CurrentCultureIgnoreCase).ToList());

        var rows = new List<ClearanceTreeRow>();
        var placed = new HashSet<int>();

        void Walk(ClearanceDivision division, int depth)
        {
            if (!placed.Add(division.Id))
            {
                return;
            }

            rows.Add(new ClearanceTreeRow(division, depth, Descendants(division.Id, children)));
            foreach (var child in children.GetValueOrDefault(division.Id) ?? [])
            {
                Walk(child, depth + 1);
            }
        }

        var roots = byId.Values
            .Where(d => d.ParentId is not { } parent || parent == d.Id || !byId.ContainsKey(parent))
            .OrderBy(Title, StringComparer.CurrentCultureIgnoreCase);
        foreach (var root in roots)
        {
            Walk(root, 0);
        }

        // Строки, до которых обход не дошёл (цикл в данных), — в конце, с верхнего уровня.
        foreach (var rest in byId.Values.Where(d => !placed.Contains(d.Id)).OrderBy(Title, StringComparer.CurrentCultureIgnoreCase))
        {
            Walk(rest, 0);
        }

        return rows;
    }

    /// <summary>
    /// Строки, видимые при поиске: совпала сама строка или кто-то из её вложенных (тогда видна вся цепочка вверх).
    /// Пустой поиск — все строки.
    /// </summary>
    public static IReadOnlyList<ClearanceTreeRow> Visible(IReadOnlyList<ClearanceTreeRow> rows, string? search)
    {
        ArgumentNullException.ThrowIfNull(rows);

        var text = search?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return rows;
        }

        var matched = rows
            .Where(r => r.Title.Contains(text, StringComparison.CurrentCultureIgnoreCase))
            .Select(r => r.Division.Id)
            .ToHashSet();
        return rows
            .Where(r => matched.Contains(r.Division.Id) || r.DescendantIds.Any(matched.Contains))
            .ToList();
    }

    private static string Title(ClearanceDivision division) => division.OwnName ?? division.Name ?? string.Empty;

    private static List<int> Descendants(int id, Dictionary<int, List<ClearanceDivision>> children)
    {
        var result = new List<int>();
        var seen = new HashSet<int> { id };
        var stack = new Stack<int>();
        stack.Push(id);
        while (stack.Count > 0)
        {
            foreach (var child in children.GetValueOrDefault(stack.Pop()) ?? [])
            {
                if (seen.Add(child.Id))
                {
                    result.Add(child.Id);
                    stack.Push(child.Id);
                }
            }
        }

        return result;
    }
}
