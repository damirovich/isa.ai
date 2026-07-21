using ISC.AI.Persistence;
using Microsoft.Extensions.Configuration;
using Shouldly;

namespace ISC.AI.UnitTests.Persistence;

/// <summary>
/// Сборка строк подключения (Э4-10, Э3-08): пароль — из per-name секрета <c>Database:Passwords:{name}</c>
/// (подключения к сторонним БД со своими кредами), иначе — из общего <c>Database:Password</c>.
/// </summary>
public sealed class ConnectionStringResolverTests
{
    private static IConfiguration Build(params KeyValuePair<string, string?>[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    [Fact(DisplayName = "Общий секрет Database:Password подмешивается в строку")]
    public void Global_password_is_applied()
    {
        var config = Build(
            new("ConnectionStrings:Core", "Server=db;Database=iscai;Username=app"),
            new("Database:Password", "secret1"));

        ConnectionStringResolver.Resolve(config, "Core").ShouldContain("Password=secret1");
    }

    [Fact(DisplayName = "Per-name секрет Database:Passwords:{name} важнее общего (чужая БД — свои креды)")]
    public void PerName_password_overrides_global()
    {
        var config = Build(
            new("ConnectionStrings:Skid", "Server=skid-db;Database=skid;Username=iscai_ro"),
            new("Database:Password", "secret1"),
            new("Database:Passwords:Skid", "skid-secret"));

        var resolved = ConnectionStringResolver.Resolve(config, "Skid");

        resolved.ShouldContain("Password=skid-secret");
        resolved.ShouldNotContain("secret1");
    }

    [Fact(DisplayName = "Без секретов строка возвращается как есть")]
    public void Without_secrets_base_string_is_returned()
    {
        var config = Build(new KeyValuePair<string, string?>(
            "ConnectionStrings:Core", "Server=db;Database=iscai;Username=app"));

        ConnectionStringResolver.Resolve(config, "Core")
            .ShouldBe("Server=db;Database=iscai;Username=app");
    }

    [Fact(DisplayName = "ResolveExternal: НЕ подмешивает общий Database:Password (пароль ядра не утекает на чужой сервер)")]
    public void ResolveExternal_never_falls_back_to_shared_password()
    {
        var config = Build(
            new("ConnectionStrings:Skid", "Server=skid-db;Database=skid;Username=iscai_ro"),
            new("Database:Password", "core-secret"));

        Should.Throw<InvalidOperationException>(() => ConnectionStringResolver.ResolveExternal(config, "Skid"));
    }

    [Fact(DisplayName = "ResolveExternal: с именным секретом работает как обычно")]
    public void ResolveExternal_uses_own_named_secret()
    {
        var config = Build(
            new("ConnectionStrings:Skid", "Server=skid-db;Database=skid;Username=iscai_ro"),
            new("Database:Password", "core-secret"),
            new("Database:Passwords:Skid", "skid-secret"));

        var resolved = ConnectionStringResolver.ResolveExternal(config, "Skid");

        resolved.ShouldContain("Password=skid-secret");
        resolved.ShouldNotContain("core-secret");
    }
}
