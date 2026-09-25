using System;
using System.Collections.Generic;
using System.IO;
using ISC.AI.Speech;
using Microsoft.Extensions.Logging;
using Shouldly;
using Xunit;

namespace ISC.AI.UnitTests.Speech;

/// <summary>
/// Уборка остатков прогонов расшифровки (ТБ-064, ADR-0026) на ВРЕМЕННОМ корне теста, а не на настоящем
/// <c>%TEMP%/iscai-speech</c>: удаляются только каталоги, не занятые замком живого прогона и не менявшиеся с
/// заданного момента; один неудаляемый каталог не останавливает остальные, неудача — в журнал с путём.
/// </summary>
public sealed class SpeechWorkDirSweeperTests : IDisposable
{
    private static readonly DateTime LongAgo = DateTime.UtcNow.AddDays(-2);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "iscai-speech-sweep-tests", Guid.NewGuid().ToString("N"));

    public SpeechWorkDirSweeperTests() => Directory.CreateDirectory(_root);

    /// <inheritdoc />
    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Временный каталог теста — не повод валить прогон.
        }
    }

    [Fact(DisplayName = "ТБ-064: каталог, не менявшийся с момента старта процесса, удаляется; свежий — нет (может быть чужим прогоном, ещё не взявшим замок)")]
    public void Stale_dirs_are_removed_and_fresh_are_kept()
    {
        var stale = RunDir("stale", LongAgo);
        var fresh = RunDir("fresh", modifiedUtc: null);

        var deleted = SpeechWorkDirSweeper.Sweep(_root, DateTime.UtcNow.AddHours(-1));

        deleted.ShouldBe(1);
        Directory.Exists(stale).ShouldBeFalse();
        Directory.Exists(fresh).ShouldBeTrue();
    }

    [Fact(DisplayName = "Свежесть — по самому позднему из каталога и его файлов: старый каталог с WAV, в который ещё пишут, не трогается")]
    public void Dir_with_recently_written_file_is_kept()
    {
        var dir = RunDir("writing", LongAgo);
        File.SetLastWriteTimeUtc(Path.Combine(dir, "audio.wav"), DateTime.UtcNow);

        SpeechWorkDirSweeper.Sweep(_root, DateTime.UtcNow.AddHours(-1)).ShouldBe(0);

        Directory.Exists(dir).ShouldBeTrue();
    }

    [Fact(DisplayName = "Замок живого прогона (другой экземпляр хоста) защищает каталог при любом возрасте; снятый замок — каталог удаляется")]
    public void Live_run_lock_protects_dir()
    {
        var live = RunDir("live", LongAgo);
        var finished = RunDir("finished", LongAgo);
        using (SpeechWorkDirSweeper.AcquireRunLock(finished))
        {
            // Прогон взял замок и завершился (погиб) — замок освобождён вместе с процессом.
        }

        using var runLock = SpeechWorkDirSweeper.AcquireRunLock(live);
        SpeechWorkDirSweeper.IsHeldByLiveRun(live).ShouldBeTrue();
        SpeechWorkDirSweeper.IsHeldByLiveRun(finished).ShouldBeFalse();

        var deleted = SpeechWorkDirSweeper.Sweep(_root, DateTime.MaxValue);

        deleted.ShouldBe(1);
        Directory.Exists(live).ShouldBeTrue();
        File.Exists(Path.Combine(live, "audio.wav")).ShouldBeTrue("WAV живого прогона не тронут");
        Directory.Exists(finished).ShouldBeFalse();
    }

    [Fact(DisplayName = "ТБ-064: неудаляемый каталог (файл держит процесс) не останавливает уборку остальных; неудача — в журнал с путём")]
    public void Locked_dir_does_not_stop_the_rest()
    {
        var first = RunDir("a-first", LongAgo);
        var blocked = RunDir("b-blocked", LongAgo);
        var last = RunDir("c-last", LongAgo);
        var logger = new RecordingLogger();

        // Не замок прогона, а обычный файл, который держит «зависший» процесс-распознаватель погибшего хоста.
        using (new FileStream(Path.Combine(blocked, "audio.wav"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            SpeechWorkDirSweeper.Sweep(_root, DateTime.MaxValue, logger);

            Directory.Exists(first).ShouldBeFalse();
            Directory.Exists(last).ShouldBeFalse();
            if (OperatingSystem.IsWindows())
            {
                // На Linux открытый файл удаляется (unlink) — там неудачи нет.
                Directory.Exists(blocked).ShouldBeTrue();
                logger.Warnings.ShouldContain(message => message.Contains(blocked, StringComparison.Ordinal));
            }
        }

        // Файл отпустили — следующая уборка забирает и его.
        SpeechWorkDirSweeper.Sweep(_root, DateTime.MaxValue);
        Directory.Exists(blocked).ShouldBeFalse();
    }

    [Fact(DisplayName = "Нет корня временных каталогов — уборка ничего не делает и не бросает")]
    public void Missing_root_is_not_an_error() =>
        SpeechWorkDirSweeper.Sweep(Path.Combine(_root, "нет"), DateTime.MaxValue).ShouldBe(0);

    [Fact(DisplayName = "Граница «остаток прошлых запусков» — момент старта текущего процесса, не позже текущего момента")]
    public void Process_start_is_in_the_past() =>
        SpeechWorkDirSweeper.ProcessStartUtc().ShouldBeLessThanOrEqualTo(DateTime.UtcNow);

    // Каталог прогона с WAV; время изменения (каталога и файла) — задано или «сейчас».
    private string RunDir(string name, DateTime? modifiedUtc)
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        var wav = Path.Combine(dir, "audio.wav");
        File.WriteAllBytes(wav, [0, 1, 2, 3]);
        if (modifiedUtc is { } time)
        {
            File.SetLastWriteTimeUtc(wav, time);
            Directory.SetLastWriteTimeUtc(dir, time);
        }

        return dir;
    }

    // Журнал, запоминающий предупреждения (сгенерированные LoggerMessage-методы пишут только при IsEnabled).
    private sealed class RecordingLogger : ILogger
    {
        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                Warnings.Add(formatter(state, exception));
            }
        }
    }
}
