using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ISC.AI.Abstractions.BackgroundTasks;
using ISC.AI.Modules.Media.Application.Features.Maintenance;
using ISC.AI.Modules.Media.Domain.Services;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Xunit;

namespace ISC.AI.UnitTests.Media;

/// <summary>
/// Дозаполнение поиска ИИ расшифровками после старта (ADR-0051): партиями в общей очереди — полная партия ставит
/// следующую в конец очереди; носитель, уже пробованный в этом прогоне, второй раз не берётся (отказ ядра не зацикливает).
/// </summary>
public sealed class TranscriptCorpusBackfillTests
{
    [Fact(DisplayName = "Полная партия индексируется и ставит следующую; пробованные не повторяются; неполная — последняя")]
    public async Task Batches_chain_and_skip_attempted()
    {
        var corpus = Substitute.For<ITranscriptCorpus>();
        var queue = new RecordingQueue();
        using var services = new ServiceCollection()
            .AddSingleton(corpus)
            .AddSingleton<IBackgroundTaskQueue>(queue)
            .BuildServiceProvider();

        // Первые 20 «не индексируются» (остаются в ожидании) — вторая партия обязана взять следующие, а не их же.
        var pending = Enumerable.Range(1, 25).ToList();
        corpus.ListPendingAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(ci => (IReadOnlyList<int>)pending.Take(ci.ArgAt<int>(0)).ToList());
        var attempted = new HashSet<int>();

        (await TranscriptCorpusBackfill.RunBatchAsync(services, attempted, CancellationToken.None)).ShouldBe(TranscriptCorpusBackfill.BatchSize);
        queue.Kinds.ShouldBe([TranscriptCorpusBackfill.TaskKind]);
        await corpus.Received(TranscriptCorpusBackfill.BatchSize).IndexAsync(Arg.Is<int>(id => id <= 20), Arg.Any<CancellationToken>());

        (await TranscriptCorpusBackfill.RunBatchAsync(services, attempted, CancellationToken.None)).ShouldBe(5);
        await corpus.Received(1).IndexAsync(25, Arg.Any<CancellationToken>());
        queue.Kinds.Count.ShouldBe(1, "неполная партия — последняя");
        attempted.Count.ShouldBe(25);
    }

    private sealed class RecordingQueue : IBackgroundTaskQueue
    {
        public List<string> Kinds { get; } = [];

        public ValueTask<Guid> EnqueueAsync(
            string kind, Func<IServiceProvider, CancellationToken, Task> work, CancellationToken cancellationToken = default)
        {
            Kinds.Add(kind);
            return ValueTask.FromResult(Guid.NewGuid());
        }
    }
}
