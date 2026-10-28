using System.Security.Cryptography;

namespace ISC.AI.Backup;

/// <summary>Итог проверки копии.</summary>
/// <param name="Manifest">Манифест; <see langword="null"/> — не прочитан.</param>
/// <param name="Problems">Что не так (пусто — копия цела).</param>
public sealed record BackupCheck(BackupManifest? Manifest, IReadOnlyList<string> Problems)
{
    /// <summary>Копия цела.</summary>
    public bool Ok => Manifest is not null && Problems.Count == 0;
}

/// <summary>
/// Проверка манифеста копии (ТБ-082, ADR-0063) без закрытого ключа: все файлы на месте, размеры и SHA-256 совпадают,
/// каждый файл зашифрован ключом из манифеста, а он — действующий (если задан ожидаемый отпечаток). Тем же проверяет
/// копию программа восстановления до расшифровки.
/// </summary>
public static class BackupVerifier
{
    /// <summary>Проверяет каталог копии.</summary>
    /// <param name="folder">Каталог копии.</param>
    /// <param name="expectedFingerprint">Отпечаток действующего ключа; <see langword="null"/> — не сверять.</param>
    /// <param name="cancellationToken">Отмена.</param>
    public static async Task<BackupCheck> VerifyAsync(string folder, string? expectedFingerprint, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        if (!Directory.Exists(folder))
        {
            return new BackupCheck(null, [$"Каталог копии не найден: {folder}"]);
        }

        BackupManifest manifest;
        try
        {
            manifest = await BackupManifest.LoadAsync(folder, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is FileNotFoundException or InvalidDataException)
        {
            return new BackupCheck(null, [exception is FileNotFoundException ? "Нет манифеста копии." : exception.Message]);
        }

        var problems = new List<string>();
        if (manifest.FormatVersion != BackupManifest.CurrentFormat)
        {
            problems.Add($"Неизвестная версия формата копии: {manifest.FormatVersion}.");
        }

        if (expectedFingerprint is not null && !string.Equals(manifest.KeyFingerprint, expectedFingerprint, StringComparison.OrdinalIgnoreCase))
        {
            problems.Add("Копия зашифрована не действующим ключом: отпечаток в манифесте не совпадает с Backup:PublicKey:Sha256.");
        }

        var referenced = manifest.Databases.Select(d => d.File).Concat(manifest.Storages.Select(s => s.File)).ToHashSet(StringComparer.Ordinal);
        foreach (var name in referenced.Where(name => manifest.Files.All(f => f.Name != name)))
        {
            problems.Add($"Файл {name} не описан в манифесте.");
        }

        foreach (var entry in manifest.Files)
        {
            var path = Path.Combine(folder, entry.Name);
            if (entry.Name.Contains('/') || entry.Name.Contains('\\') || !File.Exists(path))
            {
                problems.Add($"Нет файла {entry.Name}.");
                continue;
            }

            var length = new FileInfo(path).Length;
            if (length != entry.Bytes)
            {
                problems.Add($"Размер {entry.Name} — {length} байт, в манифесте — {entry.Bytes}.");
                continue;
            }

            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
            try
            {
                var fingerprint = await BackupCipher.ReadFingerprintAsync(stream, cancellationToken).ConfigureAwait(false);
                if (!string.Equals(fingerprint, manifest.KeyFingerprint, StringComparison.OrdinalIgnoreCase))
                {
                    problems.Add($"{entry.Name} зашифрован не тем ключом, что указан в манифесте.");
                }
            }
            catch (InvalidDataException exception)
            {
                problems.Add($"{entry.Name}: {exception.Message}");
                continue;
            }

            stream.Position = 0;
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
            if (!string.Equals(hash, entry.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                problems.Add($"Содержимое {entry.Name} изменено: SHA-256 не совпадает с манифестом.");
            }
        }

        return new BackupCheck(manifest, problems);
    }

    /// <summary>
    /// Последняя готовая копия на носителе (по имени каталога — в нём время); недописанные (<c>.partial</c>) не в счёт.
    /// </summary>
    public static string? FindLatest(string targetFolder)
    {
        if (!Directory.Exists(targetFolder))
        {
            return null;
        }

        return Directory.GetDirectories(targetFolder, BackupWriter.FolderPrefix + "*")
            .Where(dir => !dir.EndsWith(BackupWriter.PartialSuffix, StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .LastOrDefault();
    }
}
