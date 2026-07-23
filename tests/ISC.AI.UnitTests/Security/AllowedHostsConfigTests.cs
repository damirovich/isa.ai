using System.Runtime.CompilerServices;
using System.Text.Json;
using Shouldly;

namespace ISC.AI.UnitTests.Security;

/// <summary>
/// Конфигурационный аудит ТБ-010: продуктивный <c>appsettings.json</c> хоста НЕ должен задавать
/// небезопасное значение <c>AllowedHosts: "*"</c> (защита от host-header injection). Страж от регрессии:
/// падает, если подстановочный хост вернут в базовый конфиг. В Development '*' допустим и здесь не проверяется.
/// </summary>
public sealed class AllowedHostsConfigTests
{
    [Fact(DisplayName = "ТБ-010: продуктивный appsettings.json не задаёт AllowedHosts = \"*\"")]
    public void Production_appsettings_does_not_allow_wildcard_hosts()
    {
        var json = File.ReadAllText(ProductionAppSettingsPath());
        using var document = JsonDocument.Parse(json);

        document.RootElement.TryGetProperty("AllowedHosts", out var allowedHosts).ShouldBeTrue(
            "В продуктивном appsettings.json должен быть явно задан AllowedHosts (не подстановочный).");
        allowedHosts.GetString().ShouldNotBe("*",
            "ТБ-010: '*' в продуктивной конфигурации запрещён — указать реальный(е) хост(ы) контура.");
    }

    // Путь к продуктивному appsettings.json хоста — от исходника этого теста (устойчиво к рабочей папке прогона).
    // .../tests/ISC.AI.UnitTests/Security/<этот файл> → корень репозитория → src/core/ISC.AI.Web/appsettings.json.
    private static string ProductionAppSettingsPath([CallerFilePath] string thisFilePath = "")
    {
        var securityDir = Path.GetDirectoryName(thisFilePath)!;
        var repoRoot = Path.GetFullPath(Path.Combine(securityDir, "..", "..", ".."));
        return Path.Combine(repoRoot, "src", "core", "ISC.AI.Web", "appsettings.json");
    }
}
