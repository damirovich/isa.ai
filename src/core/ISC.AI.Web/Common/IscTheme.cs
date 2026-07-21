using MudBlazor;

namespace ISC.AI.Web.Common;

/// <summary>
/// Единая тёмная тема оболочки (тон прототипа: глубокий сине-графитовый фон, синий акцент).
/// Используется и основной оболочкой (<c>MainLayout</c>), и страницей входа (<c>LoginLayout</c>).
/// </summary>
public static class IscTheme
{
    /// <summary>Тема приложения.</summary>
    public static MudTheme Default { get; } = new()
    {
        PaletteDark = new PaletteDark
        {
            Primary = "#3b82f6",
            Secondary = "#22d3ee",
            Info = "#3b82f6",
            Success = "#22c55e",
            Warning = "#eab308",
            Error = "#ef4444",
            Background = "#0b1220",
            BackgroundGray = "#0b1220",
            Surface = "#111c33",
            DrawerBackground = "#0a1020",
            DrawerText = "#cbd5e1",
            DrawerIcon = "#94a3b8",
            AppbarBackground = "#0b1220",
            AppbarText = "#e2e8f0",
            TextPrimary = "#e2e8f0",
            TextSecondary = "#94a3b8",
            ActionDefault = "#94a3b8",
            Divider = "#1e293b",
            DividerLight = "#1e293b",
            LinesDefault = "#1e293b",
            TableLines = "#1e293b",
        },
        LayoutProperties = new LayoutProperties
        {
            DrawerWidthLeft = "260px",
            DefaultBorderRadius = "8px",
        },
    };
}
