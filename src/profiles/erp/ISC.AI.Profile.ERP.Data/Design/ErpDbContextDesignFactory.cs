using Microsoft.EntityFrameworkCore.Design;

namespace ISC.AI.Profile.ERP.Data.Design;

/// <summary>
/// Фабрика контекста профиля ЕРП для инструментов EF Core (<c>dotnet ef</c>) — генерация миграций схемы
/// <c>erp</c> без запуска хоста. Для <c>migrations add</c> реальное соединение не требуется; строка берётся
/// из переменной окружения <c>ISCAI_ERP_CONNECTION</c> либо несекретная заглушка (пароль в коде НЕ хранится, Э4-10).
/// </summary>
public sealed class ErpDbContextDesignFactory : IDesignTimeDbContextFactory<ErpDbContext>
{
    /// <inheritdoc />
    public ErpDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ISCAI_ERP_CONNECTION")
            ?? "Server=10.10.0.115;Database=ISC_AI;Username=postgres";

        var options = new DbContextOptionsBuilder<ErpDbContext>()
            .UseNpgsql(connectionString, npg =>
                npg.MigrationsHistoryTable("__ef_migrations_history", ErpDbContext.Schema))
            .UseSnakeCaseNamingConvention()
            .Options;

        return new ErpDbContext(options);
    }
}
