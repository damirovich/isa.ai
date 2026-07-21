using Microsoft.Extensions.Configuration;
using Npgsql;

namespace ISC.AI.Persistence;

/// <summary>
/// Сборка строки подключения к PostgreSQL из НЕсекретной базы (<c>ConnectionStrings:{name}</c> — хост/БД/
/// пользователь) и ОТДЕЛЬНОГО секрета пароля (<c>Database:Password</c> — из user-secrets в dev или переменной
/// окружения <c>Database__Password</c> в проде). Пароль в репозитории НЕ хранится (Э4-10, ТБ — секреты вне
/// конфигов в открытом виде).
/// </summary>
public static class ConnectionStringResolver
{
    /// <summary>
    /// Возвращает строку подключения: несекретная база из <c>ConnectionStrings:{name}</c> (или
    /// <paramref name="fallbackName"/>) + пароль из <c>Database:Passwords:{name}</c>, а при его
    /// отсутствии — из общего <c>Database:Password</c> (наши собственные БД Core/Inspector сидят на
    /// одном сервере и делят секрет — это ожидаемо). Если пароль отдельно не задан — строка используется
    /// как есть (пароль мог быть подставлен env-override целой строки, либо БД принимает без пароля).
    /// </summary>
    /// <exception cref="InvalidOperationException">Базовая строка подключения не задана.</exception>
    public static string Resolve(IConfiguration configuration, string name, string? fallbackName = null) =>
        Resolve(configuration, name, fallbackName, requireOwnSecret: false);

    /// <summary>
    /// Строка подключения к СТОРОННЕЙ БД (другая система, другие креды) — например, read-only учётка
    /// СКИД. В отличие от <see cref="Resolve(IConfiguration,string,string?)"/> НЕ подставляет общий
    /// <c>Database:Password</c> нашей БД молча: явно требует <c>Database:Passwords:{name}</c>, иначе
    /// падает при старте. Без этого забытый секрет отправил бы пароль ядровой БД на чужой сервер вместо
    /// понятной ошибки конфигурации (ТБ-013).
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Базовая строка подключения или именной секрет <c>Database:Passwords:{name}</c> не заданы.
    /// </exception>
    public static string ResolveExternal(IConfiguration configuration, string name) =>
        Resolve(configuration, name, fallbackName: null, requireOwnSecret: true);

    private static string Resolve(
        IConfiguration configuration, string name, string? fallbackName, bool requireOwnSecret)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var baseConnection = configuration.GetConnectionString(name)
            ?? (fallbackName is null ? null : configuration.GetConnectionString(fallbackName))
            ?? throw new InvalidOperationException(
                $"Не задана строка подключения 'ConnectionStrings:{name}'"
                + (fallbackName is null ? "." : $" (или '{fallbackName}')."));

        var password = configuration[$"Database:Passwords:{name}"];
        if (string.IsNullOrEmpty(password))
        {
            if (requireOwnSecret)
            {
                throw new InvalidOperationException(
                    $"Не задан именной секрет 'Database:Passwords:{name}' для стороннего подключения. "
                    + "Общий 'Database:Password' сюда намеренно не подставляется (ТБ-013): "
                    + "это пароль ДРУГОЙ системы, не ядровой БД.");
            }

            password = configuration["Database:Password"];
        }

        if (string.IsNullOrEmpty(password))
        {
            return baseConnection;
        }

        return new NpgsqlConnectionStringBuilder(baseConnection) { Password = password }.ConnectionString;
    }
}
