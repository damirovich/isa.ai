namespace ISC.AI.Profile.Inspector.UI;

/// <summary>
/// Элемент ряда чипов-фильтров <c>FilterChipRow</c>: значение фильтра, подпись,
/// базовый CSS-цвет чипа и количество записей в этой категории.
/// </summary>
/// <typeparam name="TValue">Тип значения фильтра (обычно доменный перечень).</typeparam>
/// <param name="Value">Значение фильтра.</param>
/// <param name="Label">Подпись чипа.</param>
/// <param name="Color">Базовый CSS-цвет чипа (hex).</param>
/// <param name="Count">Количество записей в категории (показывается в скобках).</param>
public sealed record FilterChipItem<TValue>(TValue Value, string Label, string Color, int Count)
    where TValue : struct;
