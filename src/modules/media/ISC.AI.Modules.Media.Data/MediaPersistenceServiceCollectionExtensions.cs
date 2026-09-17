using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.Media.Domain.Services;
using ISC.AI.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Pgvector.EntityFrameworkCore;

namespace ISC.AI.Modules.Media.Data;

/// <summary>Регистрация слоя данных пакета «Медиа» (схема <c>media</c>) в контейнере хоста.</summary>
public static class MediaPersistenceServiceCollectionExtensions
{
    /// <summary>Ключ конфигурации широты обхода HNSW при поиске по лицу (<c>hnsw.ef_search</c>).</summary>
    public const string EfSearchKey = "Media:Search:HnswEfSearch";

    /// <summary>
    /// Регистрирует <see cref="MediaDbContext"/> через фабрику (ТС-008) с провайдером Npgsql и pgvector.
    /// История миграций — в схеме <c>media</c>. Строка подключения — <c>ConnectionStrings:Media</c>
    /// (та же БД, что и ядро; при отсутствии — fallback на <c>Core</c>).
    /// </summary>
    /// <remarks>
    /// Нейтральные службы ядра, которыми пользуется пакет и которые обязан дать хост: <c>IFileStorage</c>,
    /// <c>IAuditWriter</c>, <c>IAccessPolicy</c>, <c>IAccessContextProvider</c> (ADR-0018).
    /// </remarks>
    public static IServiceCollection AddMediaPersistence(
        this IServiceCollection services, IConfiguration configuration)
    {
        // Секрет пароля — отдельно (Database:Password из user-secrets/env), в конфиге лишь несекретная база (Э4-10).
        var connectionString = ConnectionStringResolver.Resolve(configuration, "Media", fallbackName: "Core");

        services.AddDbContextFactory<MediaDbContext>(options =>
            options.UseNpgsql(connectionString, npg =>
                {
                    npg.MigrationsHistoryTable("__ef_migrations_history", MediaDbContext.Schema);
                    npg.UseVector();
                })
                .UseSnakeCaseNamingConvention());

        var efSearch = int.TryParse(configuration[EfSearchKey], out var parsed)
            ? parsed
            : PgVectorFaceSearch.DefaultEfSearch;

        // Порты домена пакета → реализации слоя данных (та же слоистость, что у профиля и документооборота).
        services.AddScoped<IFaceSearch>(sp => new PgVectorFaceSearch(
            sp.GetRequiredService<IDbContextFactory<MediaDbContext>>(),
            sp.GetRequiredService<IAccessPolicy>(),
            efSearch));
        services.AddScoped<IMediaStore, MediaStore>();
        services.AddScoped<IMediaPurger, MediaPurger>();
        services.AddScoped<IMediaFileAccess, MediaFileAccessResolver>();

        return services;
    }
}
