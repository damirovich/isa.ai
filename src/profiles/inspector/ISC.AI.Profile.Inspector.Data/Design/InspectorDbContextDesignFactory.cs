using Microsoft.EntityFrameworkCore.Design;

namespace ISC.AI.Profile.Inspector.Data.Design;

/// <summary>
/// Фабрика контекста профиля для инструментов EF Core (<c>dotnet ef</c>) — генерация миграций
/// схемы <c>inspector</c> без запуска хоста. Для <c>migrations add</c> реальное соединение не требуется;
/// строка берётся из переменной окружения <c>ISCAI_INSPECTOR_CONNECTION</c> либо значение-заглушка.
/// </summary>
public sealed class InspectorDbContextDesignFactory : IDesignTimeDbContextFactory<InspectorDbContext>
{
    /// <inheritdoc />
    public InspectorDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ISCAI_INSPECTOR_CONNECTION")
            ?? "Host=localhost;Port=5432;Database=isc_core;Username=postgres;Password=postgres";

        var options = new DbContextOptionsBuilder<InspectorDbContext>()
            .UseNpgsql(connectionString, npg =>
                npg.MigrationsHistoryTable("__ef_migrations_history", InspectorDbContext.Schema))
            .UseCamelCaseNamingConvention()
            .Options;

        return new InspectorDbContext(options);
    }
}
