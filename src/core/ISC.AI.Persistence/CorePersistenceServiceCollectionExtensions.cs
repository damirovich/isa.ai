using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.BackgroundTasks;
using ISC.AI.Abstractions.Conversations;
using ISC.AI.Abstractions.Corpus;
using ISC.AI.Persistence.Audit;
using ISC.AI.Persistence.BackgroundTasks;
using ISC.AI.Persistence.Conversations;
using ISC.AI.Persistence.Corpus;
using ISC.AI.Persistence.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Pgvector.EntityFrameworkCore;

namespace ISC.AI.Persistence;

/// <summary>Регистрация инфраструктуры данных ядра (схема <c>core</c>) в контейнере хоста.</summary>
public static class CorePersistenceServiceCollectionExtensions
{
    /// <summary>
    /// Регистрирует <see cref="CoreDbContext"/> через фабрику (ТС-008) с провайдером Npgsql.
    /// История миграций — в схеме <c>core</c>. Строка подключения — секция
    /// <c>ConnectionStrings:Core</c> конфигурации.
    /// </summary>
    /// <remarks>
    /// Регистрация не открывает соединение — хост стартует и без доступной БД; соединение
    /// устанавливается при первом обращении. Универсальный контекст ядра регистрирует хост;
    /// доменный контекст профиля (схема <c>inspector</c>) — сам профиль (ДОК-05, ТО-инф-01).
    /// </remarks>
    public static IServiceCollection AddCorePersistence(this IServiceCollection services, IConfiguration configuration)
    {
        // Секрет пароля — отдельно (Database:Password из user-secrets/env), в конфиге лишь несекретная база (Э4-10).
        var connectionString = ConnectionStringResolver.Resolve(configuration, "Core");

        services.AddDbContextFactory<CoreDbContext>(options =>
            options.UseNpgsql(connectionString, npg =>
                {
                    npg.MigrationsHistoryTable("__ef_migrations_history", CoreDbContext.Schema);
                    npg.UseVector(); // маппинг pgvector (ТО-инф-02)
                })
                .UseSnakeCaseNamingConvention());

        // Неизменяемый журнал аудита (ТБ-030/031), append-only с хеш-цепочкой.
        services.AddScoped<IAuditWriter, AuditWriter>();

        // Материализация флага годности чанков (Э4-02, ADR-0013): профиль ставит видимость по редакциям.
        services.AddScoped<IChunkCurrencyPort, ChunkCurrencyPort>();

        // Гарантированное удаление документа и всех производных (ТБ-064), физически, с записью в аудит.
        services.AddScoped<IDocumentPurger, DocumentPurger>();

        // Персист статусов фоновых задач (Э4-20, §5.1.4.5): переживает перезапуск, восстановление осиротевших.
        services.AddSingleton<IBackgroundTaskStore, EfBackgroundTaskStore>();

        // Per-op чтение допуска (Э3-08, ТБ-012/016): без кэша — отзыв действует немедленно.
        services.AddScoped<ClearanceAccessReader>();

        // Хранилище диалогов чата (сохранение истории общения), разграничение по владельцу-субъекту.
        services.AddScoped<IConversationStore, ConversationStore>();

        return services;
    }
}
