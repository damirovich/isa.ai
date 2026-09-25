using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
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
/// расшифровкам дела молча не находил бы в них слов.
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
    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        SweepTempFiles();
        await RecoverInterruptedAsync(cancellationToken);
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
