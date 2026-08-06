using ISC.AI.Modules.DocFlow.Domain.Enums;
using MudBlazor;

namespace ISC.AI.Modules.DocFlow.UI;

/// <summary>Русские подписи и цвета видов комментария (§4.8) — как <c>StatusLabels</c> для статусов.</summary>
public static class CommentLabels
{
    /// <summary>Отображаемое название вида комментария.</summary>
    public static string Label(this CommentType type) => type switch
    {
        CommentType.Question => "Вопрос",
        CommentType.Remark => "Замечание",
        CommentType.Approval => "Согласование",
        _ => type.ToString(),
    };

    /// <summary>Цвет чипа вида комментария (перенос раскраски СКИД).</summary>
    public static Color ChipColor(this CommentType type) => type switch
    {
        CommentType.Question => Color.Info,
        CommentType.Remark => Color.Warning,
        CommentType.Approval => Color.Success,
        _ => Color.Default,
    };
}
