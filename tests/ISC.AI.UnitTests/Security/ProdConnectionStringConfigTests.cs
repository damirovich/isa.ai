using System.Runtime.CompilerServices;
using System.Text.Json;
using Shouldly;

namespace ISC.AI.UnitTests.Security;

/// <summary>
/// Конфигурационный аудит ТБ-013/ТБ-044: строки подключения в продовом <c>appsettings.json</c> обязаны
/// требовать шифрование канала — секрет пароля и данные идентичности идут по сети. Страж от регрессии:
/// падает, если из строки уберут <c>SSL Mode=Require</c> или добавят строку без него.
/// </summary>
/// <remarks>
/// Строка к БД СКИД проверялась здесь до 2026-08-07; вместе с переходом на локальную идентичность
/// (Э4-35 §6.5) она выведена из конфигурации, и проверять больше нечего. Перечисление имён заменено
/// обходом ВСЕХ строк: так новая строка подключения не сможет появиться без шифрования просто потому,
/// что её забыли дописать в список.
/// </remarks>
public sealed class ProdConnectionStringConfigTests
{
    [Fact(DisplayName = "ТБ-013: ВСЕ строки подключения требуют шифрование канала (SSL Mode=Require)")]
    public void All_connection_strings_require_ssl()
    {
        var json = File.ReadAllText(ProductionAppSettingsPath());
        using var document = JsonDocument.Parse(json);

        var connectionStrings = document.RootElement.GetProperty("ConnectionStrings")
            .EnumerateObject()
            // Комментарии внутри секции — не строки подключения (соглашение конфигов проекта).
            .Where(property => !property.Name.StartsWith('_') && !property.Name.StartsWith('/'))
            .ToList();

        connectionStrings.ShouldNotBeEmpty();

        foreach (var property in connectionStrings)
        {
            property.Value.GetString().ShouldNotBeNull()
                .ShouldContain("SSL Mode=Require", Case.Insensitive,
                    $"Строка подключения '{property.Name}' без шифрования канала (ТБ-013).");
        }
    }

    private static string ProductionAppSettingsPath([CallerFilePath] string thisFilePath = "")
    {
        var securityDir = Path.GetDirectoryName(thisFilePath)!;
        var repoRoot = Path.GetFullPath(Path.Combine(securityDir, "..", "..", ".."));
        return Path.Combine(repoRoot, "src", "core", "ISC.AI.Web", "appsettings.json");
    }
}
