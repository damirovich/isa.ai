using System.Security.Cryptography;

namespace ISC.AI.Vision.Onnx;

/// <summary>
/// Проверка целостности файла модели по пину SHA-256 (ТИ-004, ТБ-051): модель — исполняемый
/// артефакт, подмена которого меняет поведение системы. Ошибки — явные и с указанием, что делать.
/// </summary>
public static class ModelFileIntegrity
{
    /// <summary>Возвращает путь, если файл существует и его SHA-256 совпадает с пином; иначе — исключение.</summary>
    public static string EnsureTrusted(string path, string? expectedSha256, string purpose)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException(
                $"Модель «{purpose}» не настроена: задайте путь к файлу ONNX в конфигурации Vision (файлы — из deploy/offline/models).");
        }

        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"Файл модели «{purpose}» не найден: {path}. Модели поставляются офлайн (export-vision-models.ps1, ТИ-004).", path);
        }

        if (string.IsNullOrWhiteSpace(expectedSha256))
        {
            throw new InvalidOperationException(
                $"Для модели «{purpose}» не задан пин SHA-256 (Vision:*:Sha256): без контроля целостности модель не используется (ТИ-004).");
        }

        using var stream = File.OpenRead(path);
        var actual = Convert.ToHexString(SHA256.HashData(stream));
        if (!actual.Equals(expectedSha256.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Целостность модели «{purpose}» нарушена: SHA-256 файла {path} = {actual}, ожидался {expectedSha256}. "
                + "Файл подменён или повреждён — повторите перенос по регламенту (ТБ-051).");
        }

        return path;
    }
}
