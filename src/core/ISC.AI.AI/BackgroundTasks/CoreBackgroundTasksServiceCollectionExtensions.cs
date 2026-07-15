using ISC.AI.Abstractions.BackgroundTasks;
using Microsoft.Extensions.DependencyInjection;

namespace ISC.AI.AI.BackgroundTasks;

/// <summary>Регистрация очереди фоновых ИИ-задач (Э4-20) в контейнере хоста.</summary>
public static class CoreBackgroundTasksServiceCollectionExtensions
{
    /// <summary>
    /// Регистрирует очередь, исполнитель и фоновый воркер. Персистентный <see cref="IBackgroundTaskStore"/>
    /// регистрирует слой данных (<c>AddCorePersistence</c>) — вызывать ПОСЛЕ него.
    /// </summary>
    public static IServiceCollection AddCoreBackgroundTasks(this IServiceCollection services)
    {
        services.AddSingleton<ChannelBackgroundTaskQueue>();
        services.AddSingleton<IBackgroundTaskQueue>(sp => sp.GetRequiredService<ChannelBackgroundTaskQueue>());
        services.AddSingleton<BackgroundTaskRunner>();
        services.AddHostedService<BackgroundTaskWorker>();
        return services;
    }
}
