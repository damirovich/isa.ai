using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ISC.AI.Speech;

/// <summary>
/// Уборка остатков прошлых прогонов расшифровки (ТБ-064, ADR-0026): временные каталоги
/// <c>%TEMP%/iscai-speech/&lt;guid&gt;</c> с WAV — копией звука из материала дела. Выполняется при СТАРТЕ хоста
/// (эта служба) и перед каждым прогоном (<see cref="SpeechWorkerTranscriber"/>).
/// </summary>
/// <remarks>
/// <para>ЗАЧЕМ ПРИ СТАРТЕ. Прогон удаляет свой каталог в любом исходе — но только если хост дожил до конца. При
/// перезапуске, обновлении или сбое посреди многочасовой расшифровки WAV остаётся на диске, в том числе когда
/// дело уже уничтожено (ADR-0025). Ждать следующей расшифровки нельзя: её может не быть неделями.</para>
/// <para>ЧТО СЧИТАЕТСЯ ОСТАТКОМ. Каталог удаляется, только если (1) он НЕ занят: живой прогон держит в нём
/// файл-замок <see cref="RunLockFileName"/> открытым монопольно, и прогон другого экземпляра хоста под той же
/// учётной записью не трогается; (2) последнее изменение (самое позднее из каталога и всех его файлов) РАНЬШЕ
/// старта этого процесса: своих прогонов до старта у процесса нет, а свежий каталог может принадлежать чужому
/// прогону, который ещё не успел взять замок.</para>
/// <para>Каждый каталог удаляется отдельно: один занятый (на Windows файл держит ещё не завершившийся
/// процесс-распознаватель погибшего хоста) не останавливает уборку остальных, а неудача пишется в журнал с
/// путём — гарантированное удаление должно быть проверяемым. Непрошедший каталог повторяется перед следующим
/// прогоном и при следующем старте.</para>
/// </remarks>
public sealed class SpeechWorkDirSweeper : IHostedService
{
    /// <summary>Файл-замок живого прогона в его временном каталоге.</summary>
    public const string RunLockFileName = "run.lock";

    private readonly ILogger _logger;

    /// <summary>Создаёт службу уборки.</summary>
    /// <param name="logger">Журнал приложения (необязателен: без него — молчаливая).</param>
    public SpeechWorkDirSweeper(ILogger<SpeechWorkDirSweeper>? logger = null)
    {
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    /// <inheritdoc />
    /// <remarks>Не бросает: сбой уборки не должен остановить запуск хоста — он пишется в журнал.</remarks>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        Sweep(SpeechWorkerTranscriber.WorkRoot, ProcessStartUtc(), _logger);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Удаляет в <paramref name="root"/> каталоги прогонов, которые не заняты живым прогоном и не менялись с
    /// момента <paramref name="modifiedBeforeUtc"/> (см. правила в описании класса). Не бросает: неудачи —
    /// в журнал с путём.
    /// </summary>
    /// <param name="root">Корень временных каталогов (<see cref="SpeechWorkerTranscriber.WorkRoot"/>; в тестах — свой).</param>
    /// <param name="modifiedBeforeUtc">Удаляются только каталоги, последнее изменение которых раньше этого момента.</param>
    /// <param name="logger">Журнал приложения.</param>
    /// <returns>Сколько каталогов удалено.</returns>
    public static int Sweep(string root, DateTime modifiedBeforeUtc, ILogger? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        logger ??= NullLogger.Instance;

        DirectoryInfo[] dirs;
        try
        {
            var rootInfo = new DirectoryInfo(root);
            if (!rootInfo.Exists)
            {
                return 0;
            }

            dirs = rootInfo.GetDirectories();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            SpeechTranscriberLog.SweepFailed(logger, exception, root);
            return 0;
        }

        var deleted = 0;
        foreach (var dir in dirs)
        {
            try
            {
                if (LastModifiedUtc(dir) >= modifiedBeforeUtc || IsHeldByLiveRun(dir.FullName))
                {
                    continue;
                }

                dir.Delete(recursive: true);
                deleted++;
            }
            catch (DirectoryNotFoundException)
            {
                // Каталог успел удалить его собственный прогон — убирать нечего.
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                SpeechTranscriberLog.StaleWorkDirNotDeleted(logger, exception, dir.FullName);
            }
        }

        if (deleted > 0)
        {
            SpeechTranscriberLog.StaleWorkDirsDeleted(logger, deleted, root);
        }

        return deleted;
    }

    /// <summary>
    /// Берёт замок прогона в его каталоге: файл <see cref="RunLockFileName"/>, открытый монопольно, пока прогон
    /// идёт. Уборка другого экземпляра хоста видит замок и каталог не трогает (на Linux .NET переводит
    /// монопольное открытие в блокировку <c>flock</c> — работает так же между процессами .NET).
    /// </summary>
    /// <param name="workDir">Каталог прогона (уже создан).</param>
    /// <returns>Открытый замок; освободить перед удалением каталога.</returns>
    public static FileStream AcquireRunLock(string workDir)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workDir);
        return new FileStream(Path.Combine(workDir, RunLockFileName), FileMode.Create, FileAccess.ReadWrite, FileShare.None);
    }

    /// <summary>Занят ли каталог живым прогоном: замок есть и монопольно не открывается.</summary>
    /// <param name="workDir">Каталог прогона.</param>
    public static bool IsHeldByLiveRun(string workDir)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workDir);
        var lockPath = Path.Combine(workDir, RunLockFileName);
        if (!File.Exists(lockPath))
        {
            return false;
        }

        try
        {
            using (new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                return false; // открылся — владелец замка завершился
            }
        }
        catch (FileNotFoundException)
        {
            return false; // прогон только что закончился и убрал за собой
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
        catch (IOException)
        {
            return true; // нарушение совместного доступа — замок держит живой прогон
        }
    }

    /// <summary>
    /// Момент старта текущего процесса (UTC) — граница «остаток прошлых запусков». Если ОС его не сообщает —
    /// текущий момент: уборка при старте хоста выполняется до запуска очереди, своих прогонов ещё нет.
    /// </summary>
    public static DateTime ProcessStartUtc()
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

    // Самое позднее изменение каталога и всего, что в нём лежит: WAV, в который ещё пишет ffmpeg, делает
    // каталог «свежим», даже если сам каталог создан давно.
    private static DateTime LastModifiedUtc(DirectoryInfo dir) =>
        dir.EnumerateFileSystemInfos("*", SearchOption.AllDirectories)
            .Select(entry => entry.LastWriteTimeUtc)
            .Aggregate(dir.LastWriteTimeUtc, (max, time) => time > max ? time : max);
}
