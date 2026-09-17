using ISC.AI.Abstractions.Storage;

namespace ISC.AI.Persistence.Storage;

/// <summary>
/// Локальное файловое хранилище ядра (<see cref="IFileStorage"/>, ADR-0018): корень —
/// <c>Storage:BasePath</c> конфигурации (на проде — явный путь на защищённом томе, ТБ-062).
/// Имена файлов — случайные GUID с расширением; исходное имя живёт только в БД владельца.
/// </summary>
/// <remarks>
/// Корень создаётся ЛЕНИВО при первом сохранении, а не в конструкторе: регистрация в контейнере
/// (и тесты, поднимающие контейнер) не должна оставлять каталогов на диске как побочный эффект.
/// Проверка «путь под корнем» сравнивает с корнем + разделитель: голое <c>StartsWith</c> пропустило бы
/// соседний каталог с тем же префиксом (<c>store</c> vs <c>store-other</c>).
/// </remarks>
public sealed class LocalFileStorage : IFileStorage
{
    /// <summary>Каталог по умолчанию — относительно рабочего каталога хоста (как у документооборота).</summary>
    public const string DefaultBasePath = "storage-files";

    private readonly string _root;

    /// <summary>Создаёт хранилище с корнем <paramref name="basePath"/> (относительный — от рабочего каталога).</summary>
    public LocalFileStorage(string? basePath)
    {
        // Пустая строка приравнена к «не задано»: ключ в appsettings может лежать пустым как документация.
        var configured = string.IsNullOrWhiteSpace(basePath) ? DefaultBasePath : basePath;
        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(configured));
    }

    /// <summary>Абсолютный корень хранилища (для диагностики и тестов).</summary>
    public string Root => _root;

    /// <inheritdoc />
    public async Task<string> SaveAsync(
        Stream content, string extension, string category, string subPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);

        var storedFileName = Guid.NewGuid().ToString("N") + (extension ?? string.Empty);
        var directory = ResolveDirectory(category, subPath);
        Directory.CreateDirectory(directory);

        var fullPath = Path.Combine(directory, storedFileName);
        await using var fileStream = new FileStream(
            fullPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize: 81920, useAsync: true);
        await content.CopyToAsync(fileStream, cancellationToken);
        return storedFileName;
    }

    /// <inheritdoc />
    public Task<Stream> OpenReadAsync(
        string storedFileName, string category, string subPath,
        CancellationToken cancellationToken = default)
    {
        var fullPath = ResolveFile(storedFileName, category, subPath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"Файл не найден: {category}/{subPath}/{storedFileName}", storedFileName);
        }

        return Task.FromResult<Stream>(new FileStream(
            fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 81920, useAsync: true));
    }

    /// <inheritdoc />
    public Task DeleteAsync(
        string storedFileName, string category, string subPath,
        CancellationToken cancellationToken = default)
    {
        var fullPath = ResolveFile(storedFileName, category, subPath);
        if (File.Exists(fullPath))
        {
            File.Delete(fullPath);
        }

        return Task.CompletedTask;
    }

    private string ResolveFile(string storedFileName, string category, string subPath)
    {
        // Имя в хранилище — плоское: разделители в нём означают попытку уйти из каталога владельца.
        if (string.IsNullOrWhiteSpace(storedFileName)
            || storedFileName.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0)
        {
            throw new InvalidOperationException("Недопустимое имя файла в хранилище.");
        }

        return EnsureWithinRoot(Path.Combine(ResolveDirectory(category, subPath), storedFileName));
    }

    private string ResolveDirectory(string category, string subPath) =>
        EnsureWithinRoot(Path.Combine(_root, category ?? string.Empty, subPath ?? string.Empty));

    // Защита от path traversal (инвариант порта): собранный путь обязан оставаться под корнем.
    private string EnsureWithinRoot(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var isInside = fullPath.Equals(_root, StringComparison.OrdinalIgnoreCase)
            || fullPath.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        if (!isInside)
        {
            throw new InvalidOperationException("Путь выходит за пределы файлового хранилища.");
        }

        return fullPath;
    }
}
