using Blazor.Diagrams.Core.Models;
using Blazor.Diagrams.Core.Models.Base;
using ISC.AI.Profile.Investigation.Application.Features.RelationGraph;

namespace ISC.AI.Profile.Investigation.UI;

/// <summary>
/// Подпись ребра графа связей (ADR-0045 п. 5) — подпись библиотеки с видом ребра: по нему
/// <see cref="RelationLinkLabelWidget"/> выделяет пересечение цветом.
/// </summary>
public sealed class RelationLinkLabelModel : LinkLabelModel
{
    /// <summary>Подпись ребра посередине линии.</summary>
    /// <param name="parent">Ребро.</param>
    /// <param name="content">Текст подписи.</param>
    /// <param name="kind">Вид ребра.</param>
    public RelationLinkLabelModel(BaseLinkModel parent, string content, RelationEdgeKind kind)
        : base(parent, content, null, null)
    {
        Kind = kind;
    }

    /// <summary>Вид ребра.</summary>
    public RelationEdgeKind Kind { get; }
}
