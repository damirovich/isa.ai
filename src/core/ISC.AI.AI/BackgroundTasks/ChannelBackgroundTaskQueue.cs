using System.Diagnostics.CodeAnalysis;
using System.Threading.Channels;
using ISC.AI.Abstractions.BackgroundTasks;

namespace ISC.AI.AI.BackgroundTasks;

/// <summary>Элемент очереди: идентификатор, вид и делегат-работа (в памяти, НЕ персистится).</summary>
internal sealed record BackgroundWorkItem(Guid Id, string Kind, Func<IServiceProvider, CancellationToken, Task> Work);

/// <summary>
/// Очередь фоновых задач на <see cref="Channel{T}"/> (Э4-20). Постановка сперва ПЕРСИСТИТ статус
/// <see cref="BackgroundTaskStatus.Queued"/> через <see cref="IBackgroundTaskStore"/> (виден пользователю
/// и переживает перезапуск), затем кладёт делегат в неограниченный канал. Singleton: живёт всё время
/// работы хоста; читается воркером через <see cref="Reader"/>.
/// </summary>
[SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix",
    Justification = "«Queue» — семантически точный суффикс: это реализация очереди задач.")]
public sealed class ChannelBackgroundTaskQueue(IBackgroundTaskStore store) : IBackgroundTaskQueue
{
    private readonly Channel<BackgroundWorkItem> _channel = Channel.CreateUnbounded<BackgroundWorkItem>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    /// <summary>Читатель канала для воркера (в пределах сборки ядра).</summary>
    internal ChannelReader<BackgroundWorkItem> Reader => _channel.Reader;

    /// <inheritdoc />
    public async ValueTask<Guid> EnqueueAsync(
        string kind,
        Func<IServiceProvider, CancellationToken, Task> work,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentNullException.ThrowIfNull(work);

        var id = Guid.NewGuid();

        // Сначала ПЕРСИСТ статуса (виден пользователю и переживает рестарт), затем — делегат в очередь.
        await store.CreateAsync(id, kind, cancellationToken);
        await _channel.Writer.WriteAsync(new BackgroundWorkItem(id, kind, work), cancellationToken);

        return id;
    }
}
