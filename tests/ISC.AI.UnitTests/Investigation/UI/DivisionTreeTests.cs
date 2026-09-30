using System.Collections.Generic;
using System.Linq;
using ISC.AI.Profile.Investigation.Domain.Services;
using ISC.AI.Profile.Investigation.UI;
using Shouldly;
using Xunit;

namespace ISC.AI.UnitTests.Investigation.UI;

/// <summary>
/// Дерево справочника подразделений (ТФ-АДМ-01): любая глубина (прежний список терял третий уровень), свёрнутые ветки,
/// поиск с цепочкой родителей, скрытие выключенных, сироты и циклы в данных не теряют и не зацикливают показ.
/// </summary>
public sealed class DivisionTreeTests
{
    private static readonly DivisionNode[] Nodes =
    [
        new(1, "Министерство", null, null, true, 0),
        new(2, "7 Управление", "7У", 1, true, 3),
        new(3, "1 отдел", null, 2, true, 1),
        new(4, "Группа наблюдения", "ГН", 3, true, 0),
        new(5, "Отдел кадров", null, 1, false, 0),
        new(6, "Сирота", null, 99, true, 0),
    ];

    private static readonly HashSet<int> None = [];

    [Fact(DisplayName = "Четыре уровня вложенности — в порядке «родитель, под ним дочерние» с глубиной и путём; сирота — на верхнем уровне")]
    public void Builds_any_depth()
    {
        var rows = DivisionTree.Build(Nodes, None, null, showInactive: true);

        rows.Select(r => (r.Node.Id, r.Depth)).ShouldBe([(1, 0), (2, 1), (3, 2), (4, 3), (5, 1), (6, 0)]);
        rows.Single(r => r.Node.Id == 4).Path.ShouldBe("Министерство / 7 Управление / 1 отдел / Группа наблюдения");
        rows.Single(r => r.Node.Id == 2).HasChildren.ShouldBeTrue();
        rows.Single(r => r.Node.Id == 4).HasChildren.ShouldBeFalse();
    }

    [Fact(DisplayName = "Свёрнутая ветка скрывает потомков; выключенные можно скрыть")]
    public void Collapses_and_hides_inactive()
    {
        DivisionTree.Build(Nodes, new HashSet<int> { 2 }, null, true).Select(r => r.Node.Id).ShouldBe([1, 2, 5, 6]);
        DivisionTree.Build(Nodes, None, null, showInactive: false).ShouldNotContain(r => r.Node.Id == 5);
    }

    [Fact(DisplayName = "Поиск по наименованию и коду показывает найденное с цепочкой родителей и раскрывает свёрнутое")]
    public void Search_keeps_ancestors()
    {
        DivisionTree.Build(Nodes, new HashSet<int> { 1, 2, 3 }, "гн", true).Select(r => r.Node.Id).ShouldBe([1, 2, 3, 4]);
        DivisionTree.Build(Nodes, None, "кадров", true).Select(r => r.Node.Id).ShouldBe([1, 5]);
        DivisionTree.Build(Nodes, None, "нет такого", true).ShouldBeEmpty();
    }

    [Fact(DisplayName = "Цикл в данных (А — родитель Б, Б — родитель А) не зацикливает показ и не теряет узлы")]
    public void Cycle_does_not_hang()
    {
        DivisionNode[] cyclic = [new(1, "А", null, 2, true, 0), new(2, "Б", null, 1, true, 0), new(3, "Корень", null, null, true, 0)];

        var rows = DivisionTree.Build(cyclic, None, null, true);
        rows.Select(r => r.Node.Id).ShouldBe([3, 1, 2]);
        DivisionTree.Build(cyclic, None, "А", true).Select(r => r.Node.Id).Order().ShouldBe([1, 2]);
    }
}
