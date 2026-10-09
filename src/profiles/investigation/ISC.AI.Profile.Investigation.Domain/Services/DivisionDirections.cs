using System.Collections.Generic;
using System.Linq;
using ISC.AI.Profile.Investigation.Domain.Enums;

namespace ISC.AI.Profile.Investigation.Domain.Services;

/// <summary>
/// Действующий отдел ОН/ОУ подразделения (ТЭ-008, ADR-0039): собственная отметка, а без неё — отметка ближайшего
/// вышестоящего. Так администратор отмечает отдел один раз, а группы внутри него наследуют отметку. Единственное место
/// правила: им пользуются и хранилище дел (отбор и подпись направления), и экраны (стартовая страница, справочник).
/// </summary>
/// <remarks>
/// Отметка — классификация для отбора и подписи, а не граница доступа: видимость дел решает допуск по подразделениям
/// (ТБ-020). Циклы в данных («А в Б, Б в А») не зацикливают разбор — обход ограничен числом подразделений.
/// </remarks>
public static class DivisionDirections
{
    /// <summary>Отдел каждого подразделения, у которого он определён (своей отметкой или через вышестоящее).</summary>
    /// <param name="divisions">Подразделения справочника: идентификатор, родитель, собственная отметка.</param>
    public static IReadOnlyDictionary<int, CaseDirection> Resolve(
        IEnumerable<(int Id, int? ParentId, CaseDirection? Direction)> divisions)
    {
        ArgumentNullException.ThrowIfNull(divisions);

        var byId = new Dictionary<int, (int? ParentId, CaseDirection? Direction)>();
        foreach (var (id, parentId, direction) in divisions)
        {
            byId[id] = (parentId, direction);
        }

        var result = new Dictionary<int, CaseDirection>();
        foreach (var id in byId.Keys)
        {
            int? current = id;
            for (var step = 0; current is { } at && step <= byId.Count && byId.TryGetValue(at, out var node); step++)
            {
                if (node.Direction is { } direction)
                {
                    result[id] = direction;
                    break;
                }

                current = node.ParentId;
            }
        }

        return result;
    }

    /// <inheritdoc cref="Resolve(IEnumerable{ValueTuple{int, int?, CaseDirection?}})" />
    public static IReadOnlyDictionary<int, CaseDirection> Resolve(IEnumerable<DivisionNode> divisions)
    {
        ArgumentNullException.ThrowIfNull(divisions);
        return Resolve(divisions.Select(d => (d.Id, d.ParentId, d.Direction)));
    }
}
