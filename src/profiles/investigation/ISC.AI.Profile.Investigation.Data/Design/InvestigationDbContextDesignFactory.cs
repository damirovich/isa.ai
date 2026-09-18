using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace ISC.AI.Profile.Investigation.Data.Design;

/// <summary>
/// Фабрика контекста профиля для инструментов EF Core (<c>dotnet ef</c>) — генерация миграций схемы
/// <c>investigation</c> без запуска хоста. Для <c>migrations add</c> реальное соединение не требуется;
/// строка берётся из переменной окружения <c>ISCAI_INVESTIGATION_CONNECTION</c> либо значение-заглушка.
/// </summary>
public sealed class InvestigationDbContextDesignFactory : IDesignTimeDbContextFactory<InvestigationDbContext>
{
    /// <inheritdoc />
    public InvestigationDbContext CreateDbContext(string[] args)
    {
        // Пароль в коде НЕ хранится (Э4-10): для `database update` задать полную строку в
        // ISCAI_INVESTIGATION_CONNECTION; для `migrations add` хватает несекретной заглушки без пароля.
        var connectionString = Environment.GetEnvironmentVariable("ISCAI_INVESTIGATION_CONNECTION")
            ?? "Server=10.10.0.115;Database=ISC_AI;Username=postgres";

        var options = new DbContextOptionsBuilder<InvestigationDbContext>()
            .UseNpgsql(connectionString, npg =>
                npg.MigrationsHistoryTable("__ef_migrations_history", InvestigationDbContext.Schema))
            .UseSnakeCaseNamingConvention()
            .Options;

        return new InvestigationDbContext(options);
    }
}
