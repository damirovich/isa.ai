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
    /// <paramref name="fallbackName"/>) + пароль из <c>Database:Password</c>. Если пароль отдельно не задан —
    /// строка используется как есть (пароль мог быть подставлен env-override целой строки, либо БД принимает
    /// без пароля).
    /// </summary>
    /// <exception cref="InvalidOperationException">Базовая строка подключения не задана.</exception>
    public static string Resolve(IConfiguration configuration, string name, string? fallbackName = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var baseConnection = configuration.GetConnectionString(name)
            ?? (fallbackName is null ? null : configuration.GetConnectionString(fallbackName))
            ?? throw new InvalidOperationException(
                $"Не задана строка подключения 'ConnectionStrings:{name}'"
                + (fallbackName is null ? "." : $" (или '{fallbackName}')."));

        var password = configuration["Database:Password"];
        if (string.IsNullOrEmpty(password))
        {
            return baseConnection;
        }

        return new NpgsqlConnectionStringBuilder(baseConnection) { Password = password }.ConnectionString;
    }
}
