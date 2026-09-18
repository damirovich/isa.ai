using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Shouldly;
using Xunit;

namespace ISC.AI.UnitTests.Configuration;

/// <summary>
/// Дев-настройки поставок: у каждого профиля СВОЙ файл, общий — без строк подключения (ADR-0006/0021).
/// </summary>
/// <remarks>
/// ЗАЧЕМ ЭТОТ СТРАЖ. Профили работают с РАЗНЫМИ базами: словари подразделений и режимные данные
/// смешивать нельзя (ADR-0006). Общий <c>appsettings.Development.json</c> читается ЛЮБОЙ поставкой,
/// поэтому строки подключения в нём — это строки одного профиля, молча навязанные другому: собранный
/// «ИнспекторAI» уходил в базу «Следствия», где нет схемы <c>inspector</c>, и падал на первом же
/// обращении к своим данным. Ошибка не диагностируется по коду — только по содержимому файлов,
/// поэтому проверяется здесь, а не тестом уровня кода.
///
/// Хост читает <c>appsettings.&lt;Окружение&gt;.&lt;профиль&gt;.json</c> сразу ПОСЛЕ файла окружения
/// (<c>Program.cs</c>), то есть профильные настройки перекрывают общие, но сами перекрываются
/// секретами, переменными окружения и аргументами командной строки (Э4-10). Порядок проверяется ниже
/// на той же цепочке файлов.
/// </remarks>
public sealed class ProfileSettingsTests
{
    /// <summary>Профили поставки; для каждого проверяется свой файл дев-настроек, если он есть.</summary>
    private const string InvestigationProfile = "investigation";

    [Fact(DisplayName = "Общий appsettings.Development.json не задаёт строк подключения — они у каждого профиля свои")]
    public void Shared_development_settings_declare_no_connection_strings()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(HostFile("appsettings.Development.json")));

        document.RootElement.TryGetProperty("ConnectionStrings", out _).ShouldBeFalse(
            "Строки подключения в общем файле окружения навязывают базу одного профиля всем остальным "
            + "(ADR-0006): собранный «ИнспекторAI» уходит в базу «Следствия» и падает на отсутствующей "
            + "схеме inspector. Место для них — appsettings.Development.<профиль>.json.");
    }

    [Fact(DisplayName = "Дев-настройки «Следствия» задают его базу и пути к моделям распознавания")]
    public void Investigation_development_settings_carry_its_own_database()
    {
        var path = HostFile($"appsettings.Development.{InvestigationProfile}.json");
        File.Exists(path).ShouldBeTrue(
            "Файл дев-настроек «Следствия» удалён — поставка снова возьмёт топологию Инспектора "
            + "(appsettings.json) и будет искать свои схемы в чужой базе.");

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var connections = document.RootElement.GetProperty("ConnectionStrings");

        // Четыре схемы поставки «Следствие» (core, docflow, media, investigation) — все в ЕЁ базе.
        foreach (var name in new[] { "Core", "DocFlow", "Media", "Investigation" })
        {
            connections.TryGetProperty(name, out var value).ShouldBeTrue($"Не задана строка подключения «{name}».");
            value.GetString().ShouldNotBeNullOrWhiteSpace();
        }

        // Распознавание лиц есть только у этой поставки — и настраивается вместе с её базой.
        document.RootElement.GetProperty("Vision").GetProperty("Detector")
            .GetProperty("Path").GetString().ShouldNotBeNullOrWhiteSpace();
    }

    [Theory(DisplayName = "Файл профиля перекрывает общие настройки окружения, но уступает переменным окружения и аргументам")]
    [InlineData("inspector")]
    [InlineData(InvestigationProfile)]
    public void Profile_file_overrides_environment_file_but_yields_to_overrides(string profileId)
    {
        // Та же цепочка источников, что и в Program.cs: база → окружение → ПРОФИЛЬ → переменные → аргументы.
        var configuration = new ConfigurationBuilder()
            .SetBasePath(HostDirectory())
            .AddJsonFile("appsettings.json", optional: false)
            .AddJsonFile("appsettings.Development.json", optional: false)
            .AddJsonFile($"appsettings.Development.{profileId}.json", optional: true)
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Core"] = "override" })
            .Build();

        // Последнее слово — за переопределением на месте (переменная окружения/аргумент), а не за файлом.
        configuration.GetConnectionString("Core").ShouldBe("override");

        var fromFiles = new ConfigurationBuilder()
            .SetBasePath(HostDirectory())
            .AddJsonFile("appsettings.json", optional: false)
            .AddJsonFile("appsettings.Development.json", optional: false)
            .AddJsonFile($"appsettings.Development.{profileId}.json", optional: true)
            .Build();

        var core = fromFiles.GetConnectionString("Core");
        core.ShouldNotBeNullOrWhiteSpace("Топология по умолчанию должна оставаться в appsettings.json.");

        // Профили НЕ должны делить одну базу: у «Следствия» она своя (ADR-0006), у Инспектора — из базового файла.
        var baseline = new ConfigurationBuilder()
            .SetBasePath(HostDirectory())
            .AddJsonFile("appsettings.json", optional: false)
            .Build()
            .GetConnectionString("Core");

        if (profileId == InvestigationProfile)
        {
            core.ShouldNotBe(baseline, "«Следствие» обязано подключаться к своей базе, а не к базе Инспектора.");
        }
        else
        {
            core.ShouldBe(baseline, "Инспектор берёт топологию из appsettings.json — файла окружения ему хватать не должно.");
        }
    }

    /// <summary>Каталог хоста: от исходника теста (устойчиво к рабочей папке прогона), как в AllowedHostsConfigTests.</summary>
    private static string HostDirectory([CallerFilePath] string thisFilePath = "")
    {
        var testDir = Path.GetDirectoryName(thisFilePath)!;
        var repoRoot = Path.GetFullPath(Path.Combine(testDir, "..", "..", ".."));
        return Path.Combine(repoRoot, "src", "core", "ISC.AI.Web");
    }

    private static string HostFile(string fileName) => Path.Combine(HostDirectory(), fileName);
}
