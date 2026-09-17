using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Pgvector.EntityFrameworkCore;

namespace ISC.AI.Modules.Media.Data.Design;

/// <summary>
/// Фабрика контекста пакета для инструментов EF Core (<c>dotnet ef</c>) — генерация миграций схемы
/// <c>media</c> без запуска хоста. Для <c>migrations add</c> реальное соединение не требуется;
/// строка берётся из переменной окружения <c>ISCAI_MEDIA_CONNECTION</c> либо значение-заглушка.
/// </summary>
public sealed class MediaDbContextDesignFactory : IDesignTimeDbContextFactory<MediaDbContext>
{
    /// <inheritdoc />
    public MediaDbContext CreateDbContext(string[] args)
    {
        // Пароль в коде НЕ хранится (Э4-10): для `database update` задать полную строку в
        // ISCAI_MEDIA_CONNECTION; для `migrations add` хватает несекретной заглушки без пароля.
        var connectionString = Environment.GetEnvironmentVariable("ISCAI_MEDIA_CONNECTION")
            ?? "Server=10.10.0.115;Database=ISC_AI;Username=postgres";

        var options = new DbContextOptionsBuilder<MediaDbContext>()
            .UseNpgsql(connectionString, npg =>
            {
                npg.MigrationsHistoryTable("__ef_migrations_history", MediaDbContext.Schema);
                npg.UseVector();
            })
            .UseSnakeCaseNamingConvention()
            .Options;

        return new MediaDbContext(options);
    }
}
