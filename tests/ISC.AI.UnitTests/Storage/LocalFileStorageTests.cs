using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using ISC.AI.Persistence.Storage;
using Shouldly;
using Xunit;

namespace ISC.AI.UnitTests.Storage;

/// <summary>
/// Нейтральный файловый порт ядра (ADR-0018): случайные имена, чтение сохранённого, идемпотентное
/// удаление, и главное — ни один путь не выходит за корень хранилища (защита от path traversal),
/// включая «соседний каталог с тем же префиксом», который пропустило бы голое StartsWith.
/// </summary>
public sealed class LocalFileStorageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "isc-storage-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        if (Directory.Exists(_root + "-other")) Directory.Delete(_root + "-other", recursive: true);
    }

    [Fact(DisplayName = "Сохранение даёт случайное имя с расширением; содержимое читается обратно")]
    public async Task Save_then_open_returns_same_bytes_under_random_name()
    {
        var storage = new LocalFileStorage(_root);
        var payload = Encoding.UTF8.GetBytes("кадр 00:12:34");

        var stored = await storage.SaveAsync(new MemoryStream(payload), ".bin", "media", "7");

        stored.ShouldEndWith(".bin");
        stored.ShouldNotContain("кадр");                  // исходное имя/содержимое в имени не утекает
        Path.GetFileNameWithoutExtension(stored).Length.ShouldBe(32); // GUID "N"
        await using var read = await storage.OpenReadAsync(stored, "media", "7");
        using var buffer = new MemoryStream();
        await read.CopyToAsync(buffer);
        buffer.ToArray().ShouldBe(payload);
    }

    [Fact(DisplayName = "Корень создаётся лениво: регистрация без сохранений не трогает диск")]
    public void Root_is_not_created_until_first_save()
    {
        _ = new LocalFileStorage(_root);

        Directory.Exists(_root).ShouldBeFalse();
    }

    [Fact(DisplayName = "Удаление идемпотентно: повторное удаление и удаление несуществующего — не ошибка")]
    public async Task Delete_is_idempotent()
    {
        var storage = new LocalFileStorage(_root);
        var stored = await storage.SaveAsync(new MemoryStream([1, 2, 3]), ".dat", "media", "1");

        await storage.DeleteAsync(stored, "media", "1");
        await storage.DeleteAsync(stored, "media", "1");
        await storage.DeleteAsync("nope.dat", "media", "1");

        await Should.ThrowAsync<FileNotFoundException>(() => storage.OpenReadAsync(stored, "media", "1"));
    }

    [Theory(DisplayName = "Путь за корень отклоняется: «..», абсолютный подкаталог, разделители в имени")]
    [InlineData("..", "x", "a.bin")]
    [InlineData("media", "..\\..\\etc", "a.bin")]
    [InlineData("media", "1", "..\\a.bin")]
    [InlineData("media", "1", "sub/a.bin")]
    public async Task Paths_escaping_root_are_rejected(string category, string subPath, string name)
    {
        var storage = new LocalFileStorage(_root);

        await Should.ThrowAsync<InvalidOperationException>(() => storage.OpenReadAsync(name, category, subPath));
        await Should.ThrowAsync<InvalidOperationException>(() => storage.DeleteAsync(name, category, subPath));
    }

    [Fact(DisplayName = "Абсолютный путь в подкаталоге не подменяет корень")]
    public async Task Absolute_subpath_cannot_replace_root()
    {
        var storage = new LocalFileStorage(_root);
        var elsewhere = Path.Combine(Path.GetTempPath(), "isc-elsewhere-" + Guid.NewGuid().ToString("N"));

        await Should.ThrowAsync<InvalidOperationException>(
            () => storage.SaveAsync(new MemoryStream([1]), ".bin", "media", elsewhere));

        Directory.Exists(elsewhere).ShouldBeFalse();
    }

    [Fact(DisplayName = "Соседний каталог с тем же префиксом (root-other) — не внутри корня")]
    public async Task Sibling_directory_with_same_prefix_is_outside()
    {
        var storage = new LocalFileStorage(_root);
        // category «..\<имя корня>-other» собирается в путь-сосед корня: StartsWith(root) его бы пропустил.
        var siblingCategory = Path.Combine("..", Path.GetFileName(_root) + "-other");

        await Should.ThrowAsync<InvalidOperationException>(
            () => storage.SaveAsync(new MemoryStream([1]), ".bin", siblingCategory, "1"));
    }

    // ---- ILocalFileLocator (ADR-0028): путь к оригиналу для внешнего процесса — под теми же границами корня ----

    [Fact(DisplayName = "ADR-0028: локальный путь сохранённого файла — под корнем хранилища, файл по нему читается")]
    public async Task Local_path_of_stored_file_is_under_root()
    {
        var storage = new LocalFileStorage(_root);
        var stored = await storage.SaveAsync(new MemoryStream([9, 8, 7]), ".mkv", "media-originals", "42");

        var path = storage.TryGetLocalPath(stored, "media-originals", "42");

        path.ShouldNotBeNull();
        Path.IsPathFullyQualified(path).ShouldBeTrue();
        path.ShouldStartWith(storage.Root + Path.DirectorySeparatorChar);
        Path.GetFileName(path).ShouldBe(stored);
        (await File.ReadAllBytesAsync(path)).ShouldBe(new byte[] { 9, 8, 7 });
    }

    [Fact(DisplayName = "ADR-0028: локальный путь отсутствующего файла — null (не исключение): вызывающий пойдёт через копию/404")]
    public async Task Local_path_of_missing_file_is_null()
    {
        var storage = new LocalFileStorage(_root);
        var stored = await storage.SaveAsync(new MemoryStream([1]), ".mp4", "media-originals", "1");

        storage.TryGetLocalPath("00000000000000000000000000000000.mp4", "media-originals", "1").ShouldBeNull();
        storage.TryGetLocalPath(stored, "media-originals", "2").ShouldBeNull(); // другой подкаталог — чужой носитель
        storage.TryGetLocalPath(stored, "media-faces", "1").ShouldBeNull();    // другая категория
    }

    [Theory(DisplayName = "ADR-0028: локальный путь за корень отклоняется так же, как чтение: «..», абсолютный подкаталог, разделители в имени")]
    [InlineData("..", "x", "a.mkv")]
    [InlineData("media-originals", "..\\..\\etc", "a.mkv")]
    [InlineData("media-originals", "1", "..\\a.mkv")]
    [InlineData("media-originals", "1", "sub/a.mkv")]
    [InlineData("media-originals", "1", "sub\\a.mkv")]
    [InlineData("media-originals", "1", "")]
    public void Local_path_escaping_root_is_rejected(string category, string subPath, string name)
    {
        var storage = new LocalFileStorage(_root);

        Should.Throw<InvalidOperationException>(() => storage.TryGetLocalPath(name, category, subPath));
    }

    [Fact(DisplayName = "ADR-0028: абсолютный подкаталог не подменяет корень и для локального пути")]
    public void Local_path_with_absolute_subpath_is_rejected()
    {
        var storage = new LocalFileStorage(_root);
        var elsewhere = Path.Combine(Path.GetTempPath(), "isc-elsewhere-" + Guid.NewGuid().ToString("N"));

        Should.Throw<InvalidOperationException>(() => storage.TryGetLocalPath("a.mkv", "media-originals", elsewhere));
    }
}
