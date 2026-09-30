namespace ISC.AI.Profile.Investigation.UI;

/// <summary>
/// Подсказки к неактивной кнопке: пользователь не должен гадать, почему кнопка серая. Каждая форма считает
/// «чего не хватает» одной функцией; кнопка неактивна ровно тогда, когда подсказка есть, — одно условие на оба
/// места, текст и доступность не расходятся.
/// </summary>
public static class FormHints
{
    /// <summary>
    /// «Заполните: номер, гриф» — по списку незаполненных обязательных полей; всё заполнено — <see langword="null"/>.
    /// </summary>
    /// <param name="fields">Пары «поле не заполнено» и «название поля в подсказке», в порядке формы.</param>
    public static string? Missing(params (bool IsMissing, string Name)[] fields)
    {
        var names = fields.Where(f => f.IsMissing).Select(f => f.Name).ToList();
        return names.Count == 0 ? null : "Заполните: " + string.Join(", ", names);
    }

    /// <summary>Первая непустая подсказка из нескольких проверок (порядок — как поля идут в форме).</summary>
    public static string? First(params string?[] hints) => hints.FirstOrDefault(h => h is not null);
}
