using ISC.AI.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ISC.AI.Modules.DocFlow.Data;

/// <summary>Регистрация слоя данных модуля документооборота (схема <c>docflow</c>) в контейнере хоста.</summary>
public static class DocFlowPersistenceServiceCollectionExtensions
{
    /// <summary>
    /// Регистрирует <see cref="DocFlowDbContext"/> через фабрику (ТС-008) с провайдером Npgsql.
    /// История миграций — в схеме <c>docflow</c>. Строка подключения — секция
    /// <c>ConnectionStrings:DocFlow</c> (та же БД, что и ядро; при отсутствии — fallback на <c>Core</c>).
    /// </summary>
    public static IServiceCollection AddDocFlowPersistence(
        this IServiceCollection services, IConfiguration configuration)
    {
        // Секрет пароля — отдельно (Database:Password из user-secrets/env), в конфиге лишь несекретная база (Э4-10).
        var connectionString = ConnectionStringResolver.Resolve(configuration, "DocFlow", fallbackName: "Core");

        services.AddDbContextFactory<DocFlowDbContext>(options =>
            options.UseNpgsql(connectionString, npg =>
                    npg.MigrationsHistoryTable("__ef_migrations_history", DocFlowDbContext.Schema))
                .UseSnakeCaseNamingConvention());

        // Порты домена модуля → реализации слоя данных (та же слоистость, что у профиля).
        services.AddScoped<Domain.Services.IDocumentTypeStore, DocumentTypeStore>();
        services.AddScoped<Domain.Services.IDocumentStore, DocumentStore>();

        return services;
    }
}
