using System;
using System.Collections.Generic;
using System.IO;
using ISC.AI.Modules.Media.Application;
using ISC.AI.Modules.Media.Application.Features.Import;
using Microsoft.Extensions.Configuration;
using Shouldly;
using Xunit;

namespace ISC.AI.UnitTests.Media.Import;

/// <summary>
/// Безопасность путей импорта (ADR-0041 п. 1): сервер читает только внутри каталогов из настройки, строка описи не
/// выводит за папку архива, ссылки, скрытые файлы и пересечение с хранилищем отклоняются.
/// </summary>
public sealed class MediaImportPathsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "iscai-import-test-" + Guid.NewGuid().ToString("N"));

    public MediaImportPathsTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "архив", "дело 1"));
        File.WriteAllBytes(Path.Combine(_root, "архив", "дело 1", "a.jpg"), [1, 2, 3]);
        Directory.CreateDirectory(_root + "-other");
        File.WriteAllBytes(Path.Combine(_root + "-other", "secret.jpg"), [9]);
    }

    public void Dispose()
    {
        foreach (var path in new[] { _root, _root + "-other" })
        {
            if (Directory.Exists(path))
            {
                foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }

                Directory.Delete(path, recursive: true);
            }
        }
    }

    [Theory(DisplayName = "Путь описи: абсолютный, с диском, сетевой, с «..», с недопустимым символом — недопустим")]
    [InlineData("/etc/passwd")]
    [InlineData(@"C:\Windows\win.ini")]
    [InlineData("C:file.jpg")]
    [InlineData(@"\\server\share\a.jpg")]
    [InlineData("//server/share/a.jpg")]
    [InlineData("../secret.jpg")]
    [InlineData("дело 1/../../secret.jpg")]
    [InlineData("a.jpg:stream")]
    [InlineData("   ")]
    public void Rejects_unsafe_relative_paths(string path) =>
        MediaImportPaths.NormalizeRelative(path).ShouldBeNull();

    [Theory(DisplayName = "Путь описи нормализуется: разделители «/», лишние «/» и «.» убираются")]
    [InlineData(@"дело 1\a.jpg", "дело 1/a.jpg")]
    [InlineData("./дело 1//a.jpg", "дело 1/a.jpg")]
    [InlineData(" дело 1/ a.jpg ", "дело 1/a.jpg")]
    public void Normalizes_relative_paths(string path, string expected) =>
        MediaImportPaths.NormalizeRelative(path).ShouldBe(expected);

    [Fact(DisplayName = "Соседний каталог с тем же префиксом не считается вложенным")]
    public void Sibling_with_same_prefix_is_not_under_root()
    {
        MediaImportPaths.IsUnder(Path.Combine(_root + "-other", "secret.jpg"), _root).ShouldBeFalse();
        MediaImportPaths.IsUnder(Path.Combine(_root, "архив"), _root).ShouldBeTrue();
        MediaImportPaths.IsUnder(_root, _root).ShouldBeTrue();
    }

    [Fact(DisplayName = "Файл описи внутри папки находится с размером и нормализованным путём")]
    public void Resolves_file_inside_folder()
    {
        var folder = new MediaImportPaths().ResolveFolder([_root], _root, "архив", out var problem).ShouldNotBeNull();
        problem.ShouldBeNull();

        var file = new MediaImportPaths().ResolveFile(folder, @"дело 1\a.jpg", out problem).ShouldNotBeNull();
        problem.ShouldBeNull();
        file.RelativePath.ShouldBe("дело 1/a.jpg");
        file.Length.ShouldBe(3);
    }

    [Fact(DisplayName = "Нет файла, папка вместо файла, скрытый файл — отказ с причиной")]
    public void Reports_missing_directory_and_hidden()
    {
        var folder = Path.Combine(_root, "архив");
        var paths = new MediaImportPaths();

        paths.ResolveFile(folder, "дело 1/нет.jpg", out var missing).ShouldBeNull();
        missing.ShouldBe(MediaImportPaths.NotFoundMessage);

        paths.ResolveFile(folder, "дело 1", out var directory).ShouldBeNull();
        directory.ShouldNotBeNull().ShouldContain("папка");

        if (OperatingSystem.IsWindows())
        {
            var hidden = Path.Combine(folder, "дело 1", "h.jpg");
            File.WriteAllBytes(hidden, [1]);
            File.SetAttributes(hidden, FileAttributes.Hidden);
            paths.ResolveFile(folder, "дело 1/h.jpg", out var hiddenProblem).ShouldBeNull();
            hiddenProblem.ShouldBe(MediaImportPaths.HiddenMessage);
        }
    }

    [Fact(DisplayName = "Символическая ссылка внутри архива на чужой каталог не принимается (если ОС разрешила её создать)")]
    public void Symbolic_link_in_path_is_rejected()
    {
        var link = Path.Combine(_root, "архив", "ссылка");
        try
        {
            Directory.CreateSymbolicLink(link, _root + "-other");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return; // без права создавать ссылки (Windows без режима разработчика) проверить нечем
        }

        new MediaImportPaths().ResolveFile(Path.Combine(_root, "архив"), "ссылка/secret.jpg", out var problem).ShouldBeNull();
        problem.ShouldBe(MediaImportPaths.ReparsePointMessage);
    }

    [Fact(DisplayName = "Папка задания: только из каталогов настройки, не выше каталога, должна существовать")]
    public void Folder_must_be_inside_configured_root()
    {
        var paths = new MediaImportPaths();
        paths.ResolveFolder([_root], _root + "-other", null, out var notConfigured).ShouldBeNull();
        notConfigured.ShouldNotBeNull().ShouldContain(MediaImportOptions.RootsKey);

        paths.ResolveFolder([_root], _root, "../" + Path.GetFileName(_root) + "-other", out var escape).ShouldBeNull();
        escape.ShouldNotBeNull();

        paths.ResolveFolder([_root], _root, "нет такой", out var missing).ShouldBeNull();
        missing.ShouldBe("папка не найдена");

        paths.ResolveFolder([_root], _root, null, out _).ShouldBe(Path.TrimEndingDirectorySeparator(_root));
    }

    [Fact(DisplayName = "Настройка каталогов: массив или строка через «;»; относительный путь и пересечение с хранилищем или TEMP — исключены с причиной")]
    public void Options_reject_relative_and_overlapping_roots()
    {
        // Каталоги вне TEMP (существовать не обязаны — настройка их не открывает).
        var drive = Path.GetPathRoot(Path.GetFullPath(Environment.CurrentDirectory))!;
        var archive = Path.Combine(drive, "iscai-archive-" + Guid.NewGuid().ToString("N"));
        var storage = Path.Combine(drive, "iscai-storage-" + Guid.NewGuid().ToString("N"));
        var options = MediaImportOptions.Read(Config(new Dictionary<string, string?>
        {
            [MediaImportOptions.RootsKey + ":0"] = archive,
            [MediaImportOptions.RootsKey + ":1"] = "relative/archive",
            [MediaImportOptions.RootsKey + ":2"] = Path.Combine(storage, "originals"),
            [MediaImportOptions.RootsKey + ":3"] = Path.GetTempPath(),
            [MediaImportOptions.StorageBasePathKey] = storage,
        }));

        options.UsableRoots.ShouldBe([archive]);
        options.Roots.ShouldContain(r => r.Path == "relative/archive" && r.Problem == "путь не абсолютный");
        options.Roots.ShouldContain(r => r.Problem == "пересекается с хранилищем материалов");
        options.Roots.ShouldContain(r => r.Problem == "пересекается с временным каталогом");
        options.ProcessingSlots.ShouldBe(MediaImportOptions.DefaultProcessingSlots);

        var single = MediaImportOptions.Read(Config(new Dictionary<string, string?>
        {
            [MediaImportOptions.RootsKey] = archive + ";" + archive,
            [MediaImportOptions.ProcessingSlotsKey] = "100",
        }));
        single.UsableRoots.Count.ShouldBe(1);
        single.ProcessingSlots.ShouldBe(MediaImportOptions.MaxProcessingSlots);
    }

    [Fact(DisplayName = "Без настройки каталогов импорт выключен")]
    public void Options_without_roots_have_no_roots() =>
        MediaImportOptions.Read(Config([])).UsableRoots.ShouldBeEmpty();

    private static IConfiguration Config(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();
}
