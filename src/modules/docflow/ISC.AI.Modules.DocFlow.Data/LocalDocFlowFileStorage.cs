using ISC.AI.Modules.DocFlow.Domain.Services;
using Microsoft.Extensions.Configuration;

namespace ISC.AI.Modules.DocFlow.Data;

/// <summary>
/// Локальное файловое хранилище модуля (перенос <c>LocalFileStorageService</c> СКИД): каталог
/// <c>DocFlow:Storage:BasePath</c> (по умолчанию <c>docflow-files</c> в рабочем каталоге хоста —
/// на проде задать явный путь на защищённом томе). Имена файлов — случайные GUID; исходное имя
/// живёт только в БД. Любой путь проверяется на выход за корень (защита от path traversal).
/// </summary>
public sealed class LocalDocFlowFileStorage : IDocFlowFileStorage
{
    private readonly string _basePath;

    /// <summary>Читает корень хранилища из конфигурации и создаёт его при отсутствии.</summary>
    public LocalDocFlowFileStorage(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        _basePath = Path.GetFullPath(configuration["DocFlow:Storage:BasePath"] ?? "docflow-files");
        Directory.CreateDirectory(_basePath);
    }

    /// <inheritdoc />
    public async Task<string> SaveAsync(
        Stream content, string extension, string category, string subPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);

        var storedFileName = Guid.NewGuid().ToString("N") + extension;
        var directory = ResolveWithinRoot(category, subPath);
        Directory.CreateDirectory(directory);

        var fullPath = Path.Combine(directory, storedFileName);
        await using var fileStream = File.Create(fullPath);
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

    private string ResolveFile(string storedFileName, string category, string subPath) =>
        EnsureWithinRoot(Path.Combine(ResolveWithinRoot(category, subPath), storedFileName));

    private string ResolveWithinRoot(string category, string subPath) =>
        EnsureWithinRoot(Path.Combine(_basePath, category, subPath));

    // Защита от path traversal: собранный путь обязан оставаться под корнем хранилища.
    private string EnsureWithinRoot(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!fullPath.StartsWith(_basePath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Путь выходит за пределы файлового хранилища модуля.");
        }

        return fullPath;
    }
}
