using Microsoft.EntityFrameworkCore.Design;

namespace ISC.AI.Modules.DocFlow.Data.Design;

/// <summary>
/// Фабрика контекста модуля для инструментов EF Core (<c>dotnet ef</c>) — генерация миграций схемы
/// <c>docflow</c> без запуска хоста. Для <c>migrations add</c> реальное соединение не требуется;
/// строка берётся из переменной окружения <c>ISCAI_DOCFLOW_CONNECTION</c> либо значение-заглушка.
/// </summary>
public sealed class DocFlowDbContextDesignFactory : IDesignTimeDbContextFactory<DocFlowDbContext>
{
    /// <inheritdoc />
    public DocFlowDbContext CreateDbContext(string[] args)
    {
        // Пароль в коде НЕ хранится (Э4-10): для `database update` задать полную строку в
        // ISCAI_DOCFLOW_CONNECTION; для `migrations add` хватает несекретной заглушки без пароля.
        var connectionString = Environment.GetEnvironmentVariable("ISCAI_DOCFLOW_CONNECTION")
            ?? "Server=10.10.0.115;Database=ISC_AI;Username=postgres";

        var options = new DbContextOptionsBuilder<DocFlowDbContext>()
            .UseNpgsql(connectionString, npg =>
                npg.MigrationsHistoryTable("__ef_migrations_history", DocFlowDbContext.Schema))
            .UseSnakeCaseNamingConvention()
            .Options;

        return new DocFlowDbContext(options);
    }
}
