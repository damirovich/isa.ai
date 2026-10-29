using System;
using System.Collections.Generic;
using System.IO;
using ISC.AI.Web.Common;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace ISC.AI.UnitTests.Web;

/// <summary>
/// Ключи защиты данных (cookie входа, антифорджери; ТБ-014) на сервере Linux — в постоянном каталоге
/// <c>DataProtection:KeysPath</c>: защищённое до перезапуска и из другого каталога релиза расшифровывается после;
/// без настройки регистрации нет (дев-машина — поведение ASP.NET Core по умолчанию).
/// </summary>
public sealed class DataProtectionKeysTests : IDisposable
{
    private readonly string _keys = Path.Combine(Path.GetTempPath(), "iscai-dp-keys-" + Guid.NewGuid().ToString("N"));

    [Fact(DisplayName = "Каталог задан: ключ ложится в него, а защищённое расшифровывается новым процессом (перезапуск, обновление)")]
    public void Keys_survive_restart_in_configured_folder()
    {
        string protectedText;
        using (var beforeRestart = Provider(_keys))
        {
            protectedText = Protector(beforeRestart).Protect("сеанс");
        }

        Directory.GetFiles(_keys, "key-*.xml").ShouldNotBeEmpty();
        using var afterRestart = Provider(_keys);
        Protector(afterRestart).Unprotect(protectedText).ShouldBe("сеанс");
    }

    [Fact(DisplayName = "Каталог не задан: регистраций нет — на дев-машине поведение по умолчанию")]
    public void Nothing_registered_without_setting()
    {
        var services = new ServiceCollection();

        services.AddPersistentDataProtectionKeys(Configuration(null));

        services.ShouldBeEmpty();
    }

    public void Dispose()
    {
        if (Directory.Exists(_keys))
        {
            Directory.Delete(_keys, recursive: true);
        }
    }

    private static ServiceProvider Provider(string keysPath) =>
        new ServiceCollection().AddPersistentDataProtectionKeys(Configuration(keysPath)).BuildServiceProvider();

    private static IDataProtector Protector(ServiceProvider provider) =>
        provider.GetRequiredService<IDataProtectionProvider>().CreateProtector("test");

    private static IConfiguration Configuration(string? keysPath) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [DataProtectionKeys.SettingKey] = keysPath })
            .Build();
}
