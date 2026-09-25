using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ISC.AI.Modules.Media.Application;
using ISC.AI.Modules.Media.Application.Features.Maintenance;
using ISC.AI.Modules.Media.Domain.Model;
using ISC.AI.Modules.Media.Domain.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace ISC.AI.UnitTests.Media;

/// <summary>
/// Обслуживание пакета «Медиа» при старте хоста: уборка временных копий материалов, оставшихся от аварийно
/// остановленного процесса (ТБ-064), и перевод прерванных перезапуском расшифровок/индексаций в «ошибка».
/// Корень временных файлов — свой у каждого теста: настоящий %TEMP% тесты не трогают.
/// </summary>
public sealed class MediaStartupMaintenanceTests : IDisposable
{
    private readonly string _testRoot = Path.Combine(Path.GetTempPath(), "iscai-media-unit-tests", Guid.NewGuid().ToString("N"));
    private readonly MediaTempFiles _tempFiles;
    private readonly IMediaStore _store = Substitute.For<IMediaStore>();

    public MediaStartupMaintenanceTests()
    {
        _tempFiles = new MediaTempFiles(Path.Combine(_testRoot, "iscai-media"), Path.Combine(_testRoot, "temp"));
        Directory.CreateDirectory(_tempFiles.Root);
        Directory.CreateDirectory(_tempFiles.LegacyRoot);
        _store.RecoverInterruptedAsync(Arg.Any<CancellationToken>()).Returns(new InterruptedWorkRecovery(0, 0));
    }

    [Fact(DisplayName = "ТБ-064: уборка удаляет остатки прежних запусков (весь управляемый каталог и isc-speech-*/isc-media-*/iscai-media-* в корне), свежие и чужие файлы не трогает")]
    public void Sweep_removes_leftovers_and_keeps_fresh_and_foreign_files()
    {
        var cutoff = DateTime.UtcNow;
        var old = cutoff.AddHours(-1);
        var fresh = cutoff.AddMinutes(1);

        var leftovers = new[]
        {
            Touch(_tempFiles.Root, "speech-0a1b.m4a", old),
            Touch(_tempFiles.Root, "frames-2c3d.mp4", old),
            Touch(_tempFiles.LegacyRoot, "isc-speech-4e5f.ogg", old),
            Touch(_tempFiles.LegacyRoot, "isc-media-6a7b.mp4", old),
            Touch(_tempFiles.LegacyRoot, "iscai-media-8c9d", old),
        };
        var kept = new[]
        {
            // Созданы уже этим процессом — не остатки.
            Touch(_tempFiles.Root, "speech-fresh.m4a", fresh),
            Touch(_tempFiles.LegacyRoot, "isc-media-fresh.mp4", fresh),

            // Чужие файлы корня %TEMP% — не наши, как бы стары ни были.
            Touch(_tempFiles.LegacyRoot, "other.tmp", old),
            Touch(_tempFiles.LegacyRoot, "isc-speechless.tmp", old),
        };

        var removed = _tempFiles.SweepStale(cutoff, NullLogger.Instance);

        removed.ShouldBe(leftovers.Length);
        leftovers.ShouldAllBe(path => !File.Exists(path));
        kept.ShouldAllBe(path => File.Exists(path));
    }

    [Fact(DisplayName = "Занятый файл не прерывает уборку: остальные удаляются, исключения нет")]
    public void Sweep_continues_past_locked_file()
    {
        var cutoff = DateTime.UtcNow;
        var locked = Touch(_tempFiles.Root, "speech-locked.m4a", cutoff.AddHours(-1));
        var first = Touch(_tempFiles.Root, "frames-a.mp4", cutoff.AddHours(-1));
        var second = Touch(_tempFiles.LegacyRoot, "isc-speech-b.ogg", cutoff.AddHours(-1));

        int removed;
        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            removed = _tempFiles.SweepStale(cutoff, NullLogger.Instance);
        }

        File.Exists(first).ShouldBeFalse();
        File.Exists(second).ShouldBeFalse();
        if (OperatingSystem.IsWindows())
        {
            // На Windows открытый файл не удаляется — он остаётся до следующего старта (путь — в журнале).
            File.Exists(locked).ShouldBeTrue();
            removed.ShouldBe(2);
        }
    }

    [Fact(DisplayName = "Каталогов нет — уборка ничего не делает и не падает")]
    public void Sweep_without_folders_is_noop()
    {
        var missing = new MediaTempFiles(Path.Combine(_testRoot, "нет"), Path.Combine(_testRoot, "тоже-нет"));

        missing.SweepStale(DateTime.UtcNow, NullLogger.Instance).ShouldBe(0);
    }

    [Fact(DisplayName = "Путь новой копии: управляемый каталог (создаётся), префикс конвейера, GUID, безопасное расширение")]
    public void New_path_is_inside_root_with_prefix_and_safe_extension()
    {
        Directory.Delete(_tempFiles.Root, recursive: true);

        var speech = _tempFiles.NewPath(MediaTempFiles.SpeechPrefix, "0a1b2c.m4a");
        var frames = _tempFiles.NewPath(MediaTempFiles.FramesPrefix, "x.mp4\" -y \"out");

        Directory.Exists(_tempFiles.Root).ShouldBeTrue();
        Path.GetDirectoryName(speech).ShouldBe(_tempFiles.Root);
        Path.GetFileName(speech).ShouldStartWith(MediaTempFiles.SpeechPrefix);
        Path.GetExtension(speech).ShouldBe(".m4a");
        Path.GetExtension(frames).ShouldBe(MediaFileNames.FallbackExtension);
        frames.ShouldNotContain("\"");
        speech.ShouldNotBe(_tempFiles.NewPath(MediaTempFiles.SpeechPrefix, "0a1b2c.m4a"));
        File.Exists(speech).ShouldBeFalse(); // файл пишет вызывающий
    }

    [Fact(DisplayName = "По умолчанию копии — в %TEMP%\\iscai-media, прежние файлы ищутся в корне %TEMP%")]
    public void Default_root_is_iscai_media_under_temp()
    {
        var defaults = new MediaTempFiles();

        defaults.Root.ShouldBe(Path.GetFullPath(Path.Combine(Path.GetTempPath(), MediaTempFiles.FolderName)));
        defaults.LegacyRoot.ShouldBe(Path.GetFullPath(Path.GetTempPath()));
        MediaTempFiles.RootFilePatterns.ShouldBe(["isc-speech-*", "isc-media-*", "iscai-media-*"]);
    }

    [Fact(DisplayName = "Старт хоста: остатки удалены, прерванные работы переведены хранилищем в «ошибка»")]
    public async Task Start_sweeps_temp_and_recovers_interrupted_work()
    {
        _store.RecoverInterruptedAsync(Arg.Any<CancellationToken>()).Returns(new InterruptedWorkRecovery(3, 1));
        var leftover = Touch(_tempFiles.Root, "speech-crash.m4a", DateTime.UtcNow.AddDays(-2));

        await Service().StartAsync(CancellationToken.None);

        await _store.Received(1).RecoverInterruptedAsync(Arg.Any<CancellationToken>());
        File.Exists(leftover).ShouldBeFalse();
    }

    [Fact(DisplayName = "БД недоступна на старте — только журнал, старт хоста не прерывается; уборка файлов всё равно выполнена")]
    public async Task Start_survives_database_failure()
    {
        _store.RecoverInterruptedAsync(Arg.Any<CancellationToken>())
            .Returns<InterruptedWorkRecovery>(_ => throw new InvalidOperationException("БД недоступна"));
        var leftover = Touch(_tempFiles.LegacyRoot, "isc-media-crash.mp4", DateTime.UtcNow.AddDays(-2));

        await Should.NotThrowAsync(() => Service().StartAsync(CancellationToken.None));

        File.Exists(leftover).ShouldBeFalse();
    }

    [Fact(DisplayName = "Регистрация: обслуживание при старте — IHostedService пакета; каталог временных копий — один на процесс")]
    public void Maintenance_is_registered_as_hosted_service()
    {
        var services = new ServiceCollection();
        services.AddMediaApplication(new ConfigurationBuilder().Build());

        services.ShouldContain(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(MediaStartupMaintenance));
        services.Single(d => d.ServiceType == typeof(MediaTempFiles)).Lifetime.ShouldBe(ServiceLifetime.Singleton);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        try
        {
            Directory.Delete(_testRoot, recursive: true);
        }
        catch (IOException)
        {
            // Каталог теста — не повод валить прогон.
        }
    }

    private MediaStartupMaintenance Service()
    {
        var provider = new ServiceCollection().AddScoped(_ => _store).BuildServiceProvider();
        return new MediaStartupMaintenance(
            provider.GetRequiredService<IServiceScopeFactory>(), _tempFiles, NullLogger<MediaStartupMaintenance>.Instance);
    }

    private static string Touch(string folder, string name, DateTime lastWriteUtc)
    {
        var path = Path.Combine(folder, name);
        File.WriteAllBytes(path, [1, 2, 3]);
        File.SetLastWriteTimeUtc(path, lastWriteUtc);
        return path;
    }
}
