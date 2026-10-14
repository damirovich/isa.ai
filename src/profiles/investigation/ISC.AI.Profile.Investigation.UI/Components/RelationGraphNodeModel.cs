using System;
using System.Globalization;
using System.Threading.Tasks;
using Blazor.Diagrams.Core.Geometry;
using Blazor.Diagrams.Core.Models;
using ISC.AI.Profile.Investigation.Application.Features.RelationGraph;

namespace ISC.AI.Profile.Investigation.UI;

/// <summary>
/// Узел графа связей на холсте (ADR-0045): узел ответа сервера и его состояние на странице — раскрыт, грузится,
/// подпись ведущего дела.
/// </summary>
public sealed class RelationGraphNodeModel : NodeModel
{
    /// <summary>Ширина карточки узла, px (совпадает со стилем <c>.rgraph-node</c>).</summary>
    public const double Width = 190;

    /// <summary>Узел холста для узла графа.</summary>
    /// <param name="node">Узел ответа сервера.</param>
    /// <param name="position">Положение на холсте.</param>
    public RelationGraphNodeModel(RelationNode node, Point position)
        : base(position)
    {
        ArgumentNullException.ThrowIfNull(node);
        Node = node;
        Expanded = node.Expanded;

        // Стартовый размер — чтобы рёбра сразу упирались в границу карточки (библиотека пере-измерит после рендера).
        Size = new Size(Width, 60);
    }

    /// <summary>Узел ответа сервера.</summary>
    public RelationNode Node { get; }

    /// <summary>Центр графа: не сворачивается.</summary>
    public bool IsCenter { get; init; }

    /// <summary>Соседи узла показаны.</summary>
    public bool Expanded { get; set; }

    /// <summary>Идёт раскрытие узла.</summary>
    public bool Loading { get; set; }

    /// <summary>Подпись ведущего дела («ведёт …») — у узлов дела и чужого дела.</summary>
    public string? Responsible { get; set; }

    /// <summary>Карточка фигуранта или дела; у реквизитов и чужого дела ссылки нет (ADR-0045 п. 3).</summary>
    public string? Href => Node.EntityId is not { } id
        ? null
        : Node.Kind switch
        {
            RelationNodeKind.Person => string.Create(CultureInfo.InvariantCulture, $"/persons/{id}"),
            RelationNodeKind.Case => string.Create(CultureInfo.InvariantCulture, $"/cases/{id}"),
            _ => null,
        };

    /// <summary>Щелчок по узлу (раскрыть или свернуть) — назначает страница графа.</summary>
    public Func<RelationGraphNodeModel, Task>? OnToggleRequested { get; set; }

    /// <summary>Узел можно раскрыть или свернуть щелчком.</summary>
    public bool Clickable => !IsCenter && !Loading && Node.Expandable;

    /// <summary>Передать щелчок странице.</summary>
    public Task RequestToggleAsync() => OnToggleRequested?.Invoke(this) ?? Task.CompletedTask;
}
