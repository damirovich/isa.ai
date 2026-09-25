using System.Security.Cryptography;

namespace ISC.AI.Speech;

/// <summary>
/// Проверка целостности файла модели речи по пину SHA-256 (ТИ-004, ТБ-051; ADR-0026). Модель —
/// исполняемый артефакт: её подмена меняет то, что попадёт в материалы дела как «сказанное», поэтому
/// файл без пина или с несовпавшим пином не используется. Своя копия приёма распознавания лиц
/// (<c>ISC.AI.Vision.Onnx.ModelFileIntegrity</c>) — интеграции друг на друга не ссылаются.
/// </summary>
public static class SpeechModelIntegrity
{
    /// <summary>
    /// Возвращает ПОЛНЫЙ путь к файлу, если он существует и его SHA-256 совпадает с пином; иначе — исключение
    /// с указанием, что именно не так и какой ключ конфигурации поправить.
    /// </summary>
    /// <param name="path">Путь из конфигурации (абсолютный или относительный к рабочему каталогу процесса).</param>
    /// <param name="expectedSha256">Пин SHA-256 (hex, регистр не важен).</param>
    /// <param name="purpose">Что это за файл — для текста ошибки («модель распознавания», «словарь»…).</param>
    /// <param name="pathKey">Ключ конфигурации пути — для текста ошибки.</param>
    /// <param name="shaKey">Ключ конфигурации пина — для текста ошибки.</param>
    /// <exception cref="InvalidOperationException">Путь или пин не заданы, либо хеш не совпал.</exception>
    /// <exception cref="FileNotFoundException">Файла нет по указанному пути.</exception>
    public static string EnsureTrusted(string? path, string? expectedSha256, string purpose, string pathKey, string shaKey)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException(
                $"Расшифровка не настроена: не задан путь к файлу «{purpose}» ({pathKey}). "
                + "Файлы — из офлайн-поставки deploy/offline/export-speech-models.ps1 (ADR-0026).");
        }

        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            // Относительный путь считается от рабочего каталога процесса, а он разный у Visual Studio,
            // dotnet run и службы. Сообщение обязано показать, ГДЕ искали и откуда считали, иначе «файл не
            // найден» отправляет искать несуществующую проблему.
            throw new FileNotFoundException(
                $"Файл «{purpose}» не найден: {fullPath}. Путь в конфигурации ({pathKey}): «{path}»"
                + (Path.IsPathRooted(path) ? string.Empty : $" (относительный, считается от рабочего каталога {Directory.GetCurrentDirectory()})")
                + ". Файлы поставляются офлайн (deploy/offline/export-speech-models.ps1, ТИ-004); задайте абсолютный путь"
                + " либо путь относительно рабочего каталога процесса.",
                fullPath);
        }

        if (string.IsNullOrWhiteSpace(expectedSha256))
        {
            throw new InvalidOperationException(
                $"Для файла «{purpose}» не задан пин SHA-256 ({shaKey}): без контроля целостности модель не используется (ТИ-004).");
        }

        string actual;
        using (var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan))
        {
            actual = Convert.ToHexString(SHA256.HashData(stream));
        }

        if (!actual.Equals(expectedSha256.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Целостность файла «{purpose}» нарушена: SHA-256 файла {fullPath} = {actual}, ожидался {expectedSha256.Trim()} ({shaKey}). "
                + "Файл подменён или повреждён — повторите перенос по регламенту (ТБ-051).");
        }

        return fullPath;
    }
}
