namespace ISC.AI.Modules.Admin.Application.Features.Sessions;

/// <summary>
/// Рабочее место словами по строке браузера (ADR-0060): «Edge · Windows», «Яндекс Браузер · Windows». Только для
/// показа: строку браузера клиент может подделать, поэтому решения по ней не принимаются, а полная строка видна в
/// подсказке.
/// </summary>
public static class WorkstationLabel
{
    /// <summary>Подпись, если строки браузера нет.</summary>
    public const string Unknown = "не определено";

    // Порядок важен: Edge, Opera и Яндекс несут в строке и «Chrome», Chrome — «Safari».
    private static readonly (string Token, string Name)[] Browsers =
    [
        ("YaBrowser/", "Яндекс Браузер"),
        ("Edg/", "Edge"),
        ("OPR/", "Opera"),
        ("Firefox/", "Firefox"),
        ("Chrome/", "Chrome"),
        ("Safari/", "Safari"),
    ];

    private static readonly (string Token, string Name)[] Systems =
    [
        ("Windows", "Windows"),
        ("Android", "Android"),
        ("iPhone", "iOS"),
        ("iPad", "iOS"),
        ("Mac OS X", "macOS"),
        ("Linux", "Linux"),
    ];

    /// <summary>«Браузер · система» по строке браузера; нераспознанное — «другой браузер».</summary>
    public static string Describe(string? userAgent)
    {
        if (string.IsNullOrWhiteSpace(userAgent))
        {
            return Unknown;
        }

        var browser = Browsers.FirstOrDefault(b => userAgent.Contains(b.Token, StringComparison.Ordinal)).Name ?? "другой браузер";
        var system = Systems.FirstOrDefault(s => userAgent.Contains(s.Token, StringComparison.Ordinal)).Name;
        return system is null ? browser : browser + " · " + system;
    }
}
