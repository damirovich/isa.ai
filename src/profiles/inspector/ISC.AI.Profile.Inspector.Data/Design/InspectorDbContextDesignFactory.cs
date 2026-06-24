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
            ?? "Server=10.10.0.115;Database=ISC_AI;Username=postgres;Password=Qwe123!@#";

        var options = new DbContextOptionsBuilder<InspectorDbContext>()
            .UseNpgsql(connectionString, npg =>
                npg.MigrationsHistoryTable("__ef_migrations_history", InspectorDbContext.Schema))
            .UseSnakeCaseNamingConvention()
            .Options;

        return new InspectorDbContext(options);
    }
}
