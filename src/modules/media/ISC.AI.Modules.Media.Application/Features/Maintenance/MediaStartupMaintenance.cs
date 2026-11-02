using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using ISC.AI.Abstractions.BackgroundTasks;
using ISC.AI.Modules.Media.Domain.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ISC.AI.Modules.Media.Application.Features.Maintenance;

/// <summary>
/// Обслуживание пакета «Медиа» при старте хоста: (1) удаляет временные копии материалов, оставшиеся от
/// аварийно остановленного прежнего процесса (<see cref="MediaTempFiles.SweepStale"/>, ТБ-064); (2) переводит
/// носители, чьи задачи пропали вместе с очередью в памяти, из «в очереди / в работе» в «ошибка»
/// (<see cref="IMediaStore.RecoverInterruptedAsync"/>) — иначе они висели бы так навсегда, а поиск по
/// расшифровкам дела молча не находил бы в них слов; (3) переводит в «ошибка» носители «загружен, ждёт обработки»,
/// чья задача пропала вместе с очередью, и снимает части незавершённой индексации носителей закрытых дел (ТНД-007,
/// ADR-0064 п. 9); (4) ставит в фоновую очередь дозаполнение корпуса ядра расшифровками, которых там нет или которые
/// устарели (ADR-0051, <see cref="TranscriptCorpusBackfill"/>).
/// </summary>
/// <remarks>
/// Работа — в <see cref="StartAsync"/>, а не в фоне: хост дожидается её до начала приёма запросов, поэтому ни
/// свежая загрузка, ни её временная копия не могут быть приняты за остаток прежнего процесса. Фоновая очередь
/// к этому моменту пуста (задачи ставят только запросы пользователей). Сбой уборки или БД на старте — только
/// журнал, старт хоста НЕ прерывается (как у восстановления осиротевших задач ядра, <c>BackgroundTaskWorker</c>):
/// хост стартует и без БД.
/// </remarks>
public sealed class MediaStartupMaintenance(
    IServiceScopeFactory scopeFactory,
    MediaTempFiles tempFiles,
    ILogger<MediaStartupMaintenance> logger) : IHostedService
{
    /// <summary>
    /// Причина для носителя «загружен, ждёт обработки», чья задача пропала вместе с очередью в памяти (ТНД-007,
    /// ADR-0064 п. 9): обработка не начиналась, повтор — кнопкой.
    /// </summary>
    public const string UnqueuedUploadReason =
        "не обработан: очередь обработки потеряна при перезапуске системы — нажмите «Переиндексировать»";

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        SweepTempFiles();
        await RecoverInterruptedAsync(cancellationToken);
        await RecoverUnqueuedIndexingAsync(cancellationToken);
        await EnqueueCorpusBackfillAsync(cancellationToken);
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private void SweepTempFiles()
    {
        try
        {
            // Порог — момент старта ПРОЦЕССА: всё, что записано раньше, оставлено прежним запуском.
            var removed = tempFiles.SweepStale(ProcessStartUtc(), logger);
            if (removed > 0)
            {
                MediaMaintenanceLog.TempFilesRemoved(logger, removed);
            }
        }
        catch (Exception exception)
        {
            // SweepStale сам не бросает; страховка — уборка не имеет права ронять старт.
            MediaMaintenanceLog.TempSweepFailed(logger, exception, tempFiles.Root);
        }
    }

    private async Task RecoverInterruptedAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var store = scope.ServiceProvider.GetRequiredService<IMediaStore>();
            var recovered = await store.RecoverInterruptedAsync(cancellationToken);
            if (!recovered.IsEmpty)
            {
                MediaMaintenanceLog.InterruptedRecovered(logger, recovered.Transcriptions, recovered.Indexings);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // БД недоступна на старте — не роняем хост: носители останутся в прежнем статусе до следующего старта.
            MediaMaintenanceLog.RecoveryFailed(logger, exception);
        }
    }

    private async Task EnqueueCorpusBackfillAsync(CancellationToken cancellationToken)
    {
        try
        {
            // Очередь — служба ядра; без неё (тесты пакета) дозаполнения нет. Сами партии — в фоне, старт не ждёт.
            using var scope = scopeFactory.CreateScope();
            if (scope.ServiceProvider.GetService<IBackgroundTaskQueue>() is { } queue)
            {
                await TranscriptCorpusBackfill.EnqueueAsync(queue, cancellationToken);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            MediaMaintenanceLog.CorpusBackfillFailed(logger, exception);
        }
    }

    /// <summary>
    /// ТНД-007, ADR-0064 п. 9. Носитель «загружен, ждёт обработки» после перезапуска ждал бы вечно — его задача стояла в
    /// очереди в памяти; он переводится в «ошибка» с подсказкой. Тот же статус оставляет и регламентный отказ по закрытому
    /// делу (ТБ-074) — это не сбой, и такой носитель не трогается. Части незавершённой индексации носителей, индексация
    /// которых запрещена, снимаются: авария процесса между записью части и проверкой перед публикацией оставила бы в них
    /// шаблоны закрытого дела, а повтор у закрытого дела не запускается.
    /// </summary>
    /// <remarks>
    /// Отличить сбой от регламентного отказа может только профиль (<see cref="ICaseScope"/>). Без него (тесты пакета,
    /// поставка без дел) носители не трогаются: ложная «ошибка» на регламентном отказе хуже ожидания. Сбой БД — только
    /// журнал: старт хоста не прерывается.
    /// </remarks>
    private async Task RecoverUnqueuedIndexingAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            if (scope.ServiceProvider.GetService<ICaseScope>() is not { } caseScope)
            {
                return;
            }

            var store = scope.ServiceProvider.GetRequiredService<IMediaStore>();

            var failed = 0;
            foreach (var assetId in await store.ListUnqueuedUploadsAsync(cancellationToken))
            {
                if (await caseScope.IsBiometricIndexingAllowedAsync(assetId, cancellationToken))
                {
                    await store.FailIndexingAsync(assetId, UnqueuedUploadReason, cancellationToken);
                    failed++;
                }
            }

            var discarded = 0;
            foreach (var assetId in await store.ListIndexCheckpointAssetsAsync(cancellationToken))
            {
                if (!await caseScope.IsBiometricIndexingAllowedAsync(assetId, cancellationToken))
                {
                    discarded += await store.DiscardIndexCheckpointAsync(assetId, cancellationToken);
                }
            }

            if (failed > 0 || discarded > 0)
            {
                MediaMaintenanceLog.UnqueuedRecovered(logger, failed, discarded);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            MediaMaintenanceLog.UnqueuedRecoveryFailed(logger, exception);
        }
    }

    /// <summary>Момент старта текущего процесса (UTC); если ОС его не отдаёт — «сейчас» (очередь ещё не запущена).</summary>
    private static DateTime ProcessStartUtc()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            return process.StartTime.ToUniversalTime();
        }
        catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException or Win32Exception)
        {
            return DateTime.UtcNow;
        }
    }
}
