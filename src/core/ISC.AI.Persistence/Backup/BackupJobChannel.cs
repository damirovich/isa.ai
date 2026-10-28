using System.Threading.Channels;

namespace ISC.AI.Persistence.Backup;

/// <summary>Что сделать исполнителю копий.</summary>
public enum BackupJobKind
{
    /// <summary>Сделать копию.</summary>
    Copy = 1,

    /// <summary>Проверить манифест копии.</summary>
    Verify = 2,
}

/// <summary>Задание исполнителю копий.</summary>
/// <param name="RunId">Прогон из журнала.</param>
/// <param name="Kind">Что сделать.</param>
/// <param name="UserId">Кто запустил.</param>
public sealed record BackupJob(int RunId, BackupJobKind Kind, int UserId);

/// <summary>
/// Очередь заданий копий — ОТДЕЛЬНАЯ от общей фоновой очереди (ADR-0063): та выполняет задачи по одной, и многочасовая
/// копия задержала бы обработку носителей и расшифровку. Читает её исполнитель хоста; задания живут только в памяти —
/// после перезапуска прогоны «идёт» помечаются «прервана».
/// </summary>
public sealed class BackupJobChannel
{
    private readonly Channel<BackupJob> _channel = Channel.CreateUnbounded<BackupJob>(new UnboundedChannelOptions { SingleReader = true });

    /// <summary>Поставить задание.</summary>
    public ValueTask WriteAsync(BackupJob job, CancellationToken cancellationToken = default) => _channel.Writer.WriteAsync(job, cancellationToken);

    /// <summary>Задания по мере поступления.</summary>
    public IAsyncEnumerable<BackupJob> ReadAllAsync(CancellationToken cancellationToken) => _channel.Reader.ReadAllAsync(cancellationToken);
}
