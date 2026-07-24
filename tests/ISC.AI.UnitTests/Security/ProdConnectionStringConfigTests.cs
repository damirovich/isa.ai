using System.Runtime.CompilerServices;
using System.Text.Json;
using Shouldly;

namespace ISC.AI.UnitTests.Security;

/// <summary>
/// Конфигурационный аудит ТБ-013/ТБ-044: строки подключения в продовом <c>appsettings.json</c> (в т.ч. к
/// ВНЕШНЕЙ БД СКИД) обязаны требовать шифрование канала — секрет пароля и данные идентичности идут по сети.
/// Страж от регрессии: падает, если из строки уберут <c>SSL Mode=Require</c>.
/// </summary>
public sealed class ProdConnectionStringConfigTests
{
    [Theory(DisplayName = "ТБ-013: строка подключения требует шифрование канала (SSL Mode=Require)")]
    [InlineData("Core")]
    [InlineData("Inspector")]
    [InlineData("Skid")]
    public void Connection_string_requires_ssl(string name)
    {
        var json = File.ReadAllText(ProductionAppSettingsPath());
        using var document = JsonDocument.Parse(json);

        var connectionString = document.RootElement
            .GetProperty("ConnectionStrings")
            .GetProperty(name)
            .GetString();

        connectionString.ShouldNotBeNull();
        connectionString.ShouldContain("SSL Mode=Require");
    }

    private static string ProductionAppSettingsPath([CallerFilePath] string thisFilePath = "")
    {
        var securityDir = Path.GetDirectoryName(thisFilePath)!;
        var repoRoot = Path.GetFullPath(Path.Combine(securityDir, "..", "..", ".."));
        return Path.Combine(repoRoot, "src", "core", "ISC.AI.Web", "appsettings.json");
    }
}
