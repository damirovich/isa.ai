using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Pgvector.EntityFrameworkCore;

namespace ISC.AI.Persistence.Design;

/// <summary>
/// Фабрика контекста для инструментов EF Core (<c>dotnet ef</c>) — позволяет генерировать
/// миграции без запуска хоста. Для команды <c>migrations add</c> реальное соединение не требуется;
/// строка берётся из переменной окружения <c>ISCAI_CORE_CONNECTION</c> либо значение-заглушка.
/// </summary>
public sealed class CoreDbContextDesignFactory : IDesignTimeDbContextFactory<CoreDbContext>
{
    /// <inheritdoc />
    public CoreDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ISCAI_CORE_CONNECTION")
            ?? "Server=10.10.0.115;Database=ISC_AI;Username=postgres;Password=Qwe123!@#";

        var options = new DbContextOptionsBuilder<CoreDbContext>()
            .UseNpgsql(connectionString, npg =>
            {
                npg.MigrationsHistoryTable("__ef_migrations_history", CoreDbContext.Schema);
                npg.UseVector(); // маппинг pgvector (ТО-инф-02)
            })
            .UseSnakeCaseNamingConvention()
            .Options;

        return new CoreDbContext(options);
    }
}
