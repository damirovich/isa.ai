namespace ISC.AI.Profile.Inspector.UI;

/// <summary>
/// Передача черновика из Генератора в Редактор БЕЗ копирования руками: Генератор кладёт текст
/// (и заготовку команды с перечнем неподтверждённых ссылок), переходит на «/editor», Редактор
/// забирает одноразово. Scoped-служба: в Blazor Server живёт ровно одну сессию (circuit) —
/// текст не протекает между пользователями и не переживает перезаход.
/// </summary>
public sealed class EditorDraftHandoff
{
    private string? _text;
    private string? _instruction;

    /// <summary>Положить черновик для Редактора (следующий заход на «/editor» его заберёт).</summary>
    public void Put(string text, string? instruction)
    {
        _text = text;
        _instruction = instruction;
    }

    /// <summary>Забрать черновик ОДНОРАЗОВО: повторный заход на страницу начнётся с чистого листа.</summary>
    public (string Text, string? Instruction)? Take()
    {
        if (_text is null)
        {
            return null;
        }

        var handoff = (_text, _instruction);
        _text = null;
        _instruction = null;
        return handoff;
    }
}
