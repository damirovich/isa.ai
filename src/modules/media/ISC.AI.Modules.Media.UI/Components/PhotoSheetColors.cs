using System;
using System.Collections.Generic;
using System.Linq;

namespace ISC.AI.Modules.Media.UI;

/// <summary>
/// Палитра подписей фототаблицы (ADR-0057): цвет текста и подложки выбирается из неё; сервер принимает любой
/// <c>#RRGGBB</c>, палитра — удобство страницы, а не ограничение.
/// </summary>
public static class PhotoSheetColors
{
    /// <summary>Чёрный.</summary>
    public const string Black = "#000000";

    /// <summary>Белый.</summary>
    public const string White = "#FFFFFF";

    /// <summary>Цвета палитры: название и значение.</summary>
    public static IReadOnlyList<(string Name, string Hex)> Palette { get; } =
    [
        ("Чёрный", Black),
        ("Белый", White),
        ("Красный", "#C62828"),
        ("Синий", "#1565C0"),
        ("Зелёный", "#2E7D32"),
        ("Жёлтый", "#FBC02D"),
        ("Серый", "#9E9E9E"),
    ];

    /// <summary>Название цвета палитры; цвет вне палитры — его значение.</summary>
    public static string Name(string? hex) =>
        Palette.FirstOrDefault(c => string.Equals(c.Hex, hex, StringComparison.OrdinalIgnoreCase)).Name ?? hex ?? string.Empty;
}
