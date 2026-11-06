using System.Security.Cryptography;

namespace ISC.AI.Backup;

/// <summary>Итог проверки пакета.</summary>
/// <param name="Manifest">Манифест — если прочитан и подпись верна; иначе <see langword="null"/>.</param>
/// <param name="Problems">Что не так; пусто — пакет цел.</param>
public sealed record SealedPackageCheck(SealedPackageManifest? Manifest, IReadOnlyList<string> Problems)
{
    /// <summary>Пакет цел: подпись верна, состав и хеши совпали.</summary>
    public bool Ok => Manifest is not null && Problems.Count == 0;
}

/// <summary>
/// Проверка пакета без ключа получателя (ТБ-083, ADR-0068): подпись манифеста открытым ключом источника, состав каталога
/// (ни лишнего, ни недостающего файла), объём, SHA-256 и сигнатура каждого файла, отпечаток ключа получателя в его заголовке.
/// Её выполняет импорт, и её же можно запустить на шлюзе проверки файлов.
/// </summary>
public static class SealedPackageVerifier
{
    /// <summary>Проверяет каталог пакета.</summary>
    /// <param name="folder">Каталог пакета.</param>
    /// <param name="sourceKey">Открытый ключ подписи источника — тот, чей отпечаток закреплён у получателя.</param>
    /// <param name="format">Формат файлов пакета.</param>
    /// <param name="expectedRecipientFingerprint">Отпечаток своего ключа получателя: пакет для другого — отказ; <see langword="null"/> — не сверять.</param>
    /// <param name="cancellationToken">Отмена.</param>
    public static async Task<SealedPackageCheck> VerifyAsync(
        string folder, RSA sourceKey, SealedFormat format, string? expectedRecipientFingerprint = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        ArgumentNullException.ThrowIfNull(sourceKey);
        ArgumentNullException.ThrowIfNull(format);

        var manifestPath = Path.Combine(folder, SealedPackageManifest.FileName);
        var signaturePath = Path.Combine(folder, SealedPackageManifest.SignatureFileName);
        if (!File.Exists(manifestPath) || !File.Exists(signaturePath))
        {
            return Fail("В каталоге нет манифеста пакета или его подписи — это не пакет обмена или он не дописан.");
        }

        // Подпись — над ТЕМИ ЖЕ байтами, что лежат на носителе: до неё манифесту не верим ни в чём.
        var bytes = await File.ReadAllBytesAsync(manifestPath, cancellationToken).ConfigureAwait(false);
        byte[] signature;
        try
        {
            signature = Convert.FromBase64String((await File.ReadAllTextAsync(signaturePath, cancellationToken).ConfigureAwait(false)).Trim());
        }
        catch (FormatException)
        {
            return Fail("Подпись манифеста повреждена (не Base64).");
        }

        if (!SealedSignature.Verify(bytes, signature, sourceKey))
        {
            return Fail(
                $"Подпись манифеста не сходится с ключом источника {BackupKeys.FingerprintHex(sourceKey)}: пакет подписан другим ключом или изменён.");
        }

        SealedPackageManifest manifest;
        try
        {
            manifest = SealedPackageManifest.FromBytes(bytes);
        }
        catch (InvalidDataException exception)
        {
            return Fail(exception.Message);
        }

        var problems = new List<string>();
        if (manifest.FormatVersion != SealedPackageManifest.CurrentFormat)
        {
            problems.Add($"Версия формата пакета {manifest.FormatVersion}: эта версия системы читает {SealedPackageManifest.CurrentFormat}.");
        }

        if (expectedRecipientFingerprint is not null
            && !string.Equals(manifest.RecipientKeyFingerprint, expectedRecipientFingerprint, StringComparison.OrdinalIgnoreCase))
        {
            problems.Add($"Пакет зашифрован для другого ключа: {manifest.RecipientKeyFingerprint}, ваш — {expectedRecipientFingerprint}.");
        }

        // Состав: каждый файл манифеста на месте, лишних нет (подложенный файл — тоже отказ).
        var listed = new HashSet<string>(manifest.Files.Select(f => f.Name), StringComparer.Ordinal);
        if (listed.Count != manifest.Files.Count)
        {
            problems.Add("Один и тот же файл указан в манифесте дважды.");
        }

        foreach (var path in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(folder, path).Replace(Path.DirectorySeparatorChar, '/');
            if (relative is not (SealedPackageManifest.FileName or SealedPackageManifest.SignatureFileName) && !listed.Contains(relative))
            {
                problems.Add($"Лишний файл вне манифеста: {relative}.");
            }
        }

        foreach (var file in manifest.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!SealedPackageWriter.IsSafeName(file.Name))
            {
                problems.Add($"Недопустимое имя файла в манифесте: {file.Name}.");
                continue;
            }

            var path = Path.Combine(folder, file.Name.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path))
            {
                problems.Add($"Нет файла {file.Name}.");
                continue;
            }

            await using var stream = File.OpenRead(path);
            if (stream.Length != file.Bytes)
            {
                problems.Add($"{file.Name}: объём {stream.Length} байт, в манифесте {file.Bytes}.");
                continue;
            }

            try
            {
                var fingerprint = await SealedFile.ReadFingerprintAsync(stream, format, cancellationToken).ConfigureAwait(false);
                if (!string.Equals(fingerprint, manifest.RecipientKeyFingerprint, StringComparison.OrdinalIgnoreCase))
                {
                    problems.Add($"{file.Name}: зашифрован ключом {fingerprint}, в манифесте {manifest.RecipientKeyFingerprint}.");
                }
            }
            catch (InvalidDataException exception)
            {
                problems.Add($"{file.Name}: {exception.Message}");
                continue;
            }

            stream.Position = 0;
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
            if (!string.Equals(hash, file.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                problems.Add($"{file.Name}: SHA-256 не совпал с манифестом — файл изменён или повреждён.");
            }
        }

        return new SealedPackageCheck(manifest, problems);
    }

    private static SealedPackageCheck Fail(string problem) => new(null, [problem]);
}
