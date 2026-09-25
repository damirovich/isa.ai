using System;
using System.Globalization;

namespace ISC.AI.Evals.Speech;

/// <summary>Форматирование чисел, долей и длительностей в отчётах оценки расшифровки.</summary>
internal static class SpeechEvalFormat
{
    /// <summary>Прочерк для неопределённого значения.</summary>
    public const string Missing = "—";

    /// <summary>
    /// Русская запись чисел для Markdown-отчёта (запятая, пробел между разрядами) — собрана из инвариантной,
    /// чтобы не зависеть от установленных на сервере культур.
    /// </summary>
    private static readonly NumberFormatInfo Russian = CreateRussian();

    /// <summary>Доля как процент: «8,1 %».</summary>
    public static string Percent(double? rate) =>
        rate is { } value ? (value * 100).ToString("0.0", Russian) + " %" : Missing;

    /// <summary>Коэффициент (RTF): «0,087»; <paramref name="approximate"/> — со знаком «≈».</summary>
    public static string Factor(double? factor, bool approximate = false) =>
        factor is { } value ? (approximate ? "≈" : string.Empty) + value.ToString("0.000", Russian) : Missing;

    /// <summary>Число с одним знаком после запятой: «0,7»; неопределённое — прочерк.</summary>
    public static string Number(double? value) =>
        value is { } number ? number.ToString("0.0", Russian) : Missing;

    /// <summary>Целое с разрядами: «12 345».</summary>
    public static string Count(int value) => value.ToString("#,0", Russian);

    /// <summary>Длительность: «1:02:03» или «2:05»; неизвестная — прочерк.</summary>
    public static string Duration(TimeSpan? duration)
    {
        if (duration is not { } value)
        {
            return Missing;
        }

        var totalHours = (int)value.TotalHours;
        return totalHours > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{totalHours}:{value.Minutes:00}:{value.Seconds:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{value.Minutes}:{value.Seconds:00}");
    }

    /// <summary>Таймкод фрагмента: «00:01:02.345».</summary>
    public static string Timecode(long milliseconds)
    {
        var value = TimeSpan.FromMilliseconds(Math.Max(0, milliseconds));
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{(int)value.TotalHours:00}:{value.Minutes:00}:{value.Seconds:00}.{value.Milliseconds:000}");
    }

    /// <summary>Число для CSV в заданной культуре; неопределённое — пустое поле.</summary>
    public static string Csv(double? value, string format, IFormatProvider provider) =>
        value is { } number ? number.ToString(format, provider) : string.Empty;

    /// <summary>Ячейка Markdown-таблицы: без переводов строк, вертикальная черта экранирована.</summary>
    public static string Cell(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? string.Empty
            : text.Replace("\r", " ", StringComparison.Ordinal)
                .Replace("\n", " ", StringComparison.Ordinal)
                .Replace("|", "\\|", StringComparison.Ordinal)
                .Trim();

    private static NumberFormatInfo CreateRussian()
    {
        var format = (NumberFormatInfo)NumberFormatInfo.InvariantInfo.Clone();
        format.NumberDecimalSeparator = ",";
        format.NumberGroupSeparator = " ";
        return NumberFormatInfo.ReadOnly(format);
    }
}
