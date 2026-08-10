using MudBlazor;

namespace ISC.AI.Modules.DocFlow.UI;

/// <summary>Общие настройки диалогов секции назначений — один вид у всех пяти.</summary>
public static class AssignmentDialogs
{
    /// <summary>Узкий диалог во всю ширину колонки, закрывается по Escape.</summary>
    public static readonly DialogOptions Options =
        new() { MaxWidth = MaxWidth.ExtraSmall, FullWidth = true, CloseOnEscapeKey = true };
}
