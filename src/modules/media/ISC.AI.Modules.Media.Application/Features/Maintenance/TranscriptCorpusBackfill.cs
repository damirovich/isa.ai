using ISC.AI.Abstractions.BackgroundTasks;
using ISC.AI.Modules.Media.Domain.Services;
using Microsoft.Extensions.DependencyInjection;

namespace ISC.AI.Modules.Media.Application.Features.Maintenance;

/// <summary>
/// Дозаполнение корпуса ядра готовыми расшифровками (ADR-0051): после старта хоста — расшифровки, сделанные до
/// появления индексации или не проиндексированные из-за сбоя узла ИИ, и устаревшие (расшифрованы заново).
/// </summary>
/// <remarks>
/// Работает ПАРТИЯМИ в общей фоновой очереди: партия — одна задача, следующая ставится в конец очереди. Так архив из
/// тысяч записей не задерживает обработку новых материалов следователей: их задачи встают между партиями. Носитель,
/// который в этом прогоне не проиндексировался (отказ ядра), повторно не берётся до следующего старта — иначе
/// партии крутились бы на нём бесконечно.
/// </remarks>
public static class TranscriptCorpusBackfill
{
    /// <summary>Вид задачи фоновой очереди.</summary>
    public const string TaskKind = "media:transcript-corpus-backfill";

    /// <summary>Носителей в одной партии.</summary>
    public const int BatchSize = 20;

    /// <summary>Поставить первую партию.</summary>
    public static ValueTask<Guid> EnqueueAsync(IBackgroundTaskQueue queue, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(queue);
        var attempted = new HashSet<int>();
        return queue.EnqueueAsync(TaskKind, (sp, ct) => RunBatchAsync(sp, attempted, ct), cancellationToken);
    }

    /// <summary>
    /// Одна партия: ещё не пробованные носители из ожидающих индексации; остались — следующая партия в конец очереди.
    /// </summary>
    /// <returns>Сколько носителей обработано в партии.</returns>
    public static async Task<int> RunBatchAsync(IServiceProvider services, ISet<int> attempted, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(attempted);

        var corpus = services.GetRequiredService<ITranscriptCorpus>();
        var pending = await corpus.ListPendingAsync(BatchSize + attempted.Count, cancellationToken);
        var batch = pending.Where(id => !attempted.Contains(id)).Take(BatchSize).ToList();
        foreach (var assetId in batch)
        {
            attempted.Add(assetId);
            await corpus.IndexAsync(assetId, cancellationToken);
        }

        if (batch.Count == BatchSize && services.GetService<IBackgroundTaskQueue>() is { } queue)
        {
            await queue.EnqueueAsync(TaskKind, (sp, ct) => RunBatchAsync(sp, attempted, ct), cancellationToken);
        }

        return batch.Count;
    }
}
