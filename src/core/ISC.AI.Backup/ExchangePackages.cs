using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using ISC.AI.Abstractions.Exchange;

namespace ISC.AI.Backup;

/// <summary>
/// Пакет обмена ядра (ADR-0068) поверх запечатанного пакета (<see cref="SealedPackageWriter"/>, формат
/// <see cref="SealedFormat.Exchange"/>): ключи по закреплённым отпечаткам, сборка, уборка недописанных.
/// </summary>
/// <remarks>
/// ИНВАРИАНТ (ТБ-083): отпечатки ключей и список файлов в манифесте берутся из ключей, которыми ядро шифровало и подписывало,
/// и из файлов, которые оно записало (<see cref="IExchangePackages"/>); формат — только «обмен», не «резервная копия».
/// </remarks>
public sealed class ExchangePackages : IExchangePackages
{
    /// <summary>Назначение ключей в сообщениях об ошибках.</summary>
    public const string KeyPurpose = "обмена";

    /// <inheritdoc />
    public string Fingerprint(RSA key) => BackupKeys.FingerprintHex(key);

    /// <inheritdoc />
    public RSA LoadRecipientKey(string pemPath, string pinnedSha256) =>
        LoadPinnedPublicKey(pemPath, pinnedSha256, "ключа получателя", "Ключ получателя");

    /// <inheritdoc />
    public RSA LoadSigningKey(string pemPath, string pinnedSha256, ReadOnlySpan<char> password) =>
        LoadPinnedPrivateKey(pemPath, pinnedSha256, password, "ключа подписи", "Пароль ключа подписи", "Ключ подписи");

    /// <inheritdoc />
    public RSA LoadSourceKey(string pemPath, string pinnedSha256) =>
        LoadPinnedPublicKey(pemPath, pinnedSha256, "ключа источника", "Ключ подписи источника");

    /// <inheritdoc />
    public RSA LoadReceivingKey(string pemPath, string pinnedSha256, ReadOnlySpan<char> password) =>
        LoadPinnedPrivateKey(pemPath, pinnedSha256, password, "ключа приёма", "Пароль ключа приёма", "Ключ приёма");

    /// <inheritdoc />
    public IReadOnlyList<string> ListPackages(string folder, string folderPrefix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folderPrefix);
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            return [];
        }

        // Недописанный пакет (.partial) — не пакет: его не показывают и не принимают.
        return Directory.EnumerateDirectories(folder, folderPrefix + "*")
            .Select(Path.GetFileName)
            .OfType<string>()
            .Where(name => !name.EndsWith(SealedPackageWriter.PartialSuffix, StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    /// <inheritdoc />
    public async Task<ExchangePackageHeader> ReadHeaderAsync(
        string packageFolder, Func<string, RSA?> sourceKeyFor, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageFolder);
        ArgumentNullException.ThrowIfNull(sourceKeyFor);
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(packageFolder));
        var manifestPath = Path.Combine(packageFolder, SealedPackageManifest.FileName);
        var signaturePath = Path.Combine(packageFolder, SealedPackageManifest.SignatureFileName);
        if (!File.Exists(manifestPath) || !File.Exists(signaturePath))
        {
            return new ExchangePackageHeader(name, null, null, "Нет манифеста пакета или его подписи — это не пакет обмена или он не дописан.");
        }

        // Манифесту до проверки подписи не верят ни в чём: из него берётся только имя источника — чтобы найти ключ, которым
        // подпись и проверяется. Чужой манифест с именем настроенного источника подпись этого источника не пройдёт.
        var bytes = await File.ReadAllBytesAsync(manifestPath, cancellationToken).ConfigureAwait(false);
        SealedPackageManifest claimed;
        try
        {
            claimed = SealedPackageManifest.FromBytes(bytes);
        }
        catch (InvalidDataException exception)
        {
            return new ExchangePackageHeader(name, null, null, exception.Message);
        }

        byte[] signature;
        try
        {
            signature = Convert.FromBase64String(
                (await File.ReadAllTextAsync(signaturePath, cancellationToken).ConfigureAwait(false)).Trim());
        }
        catch (FormatException)
        {
            return new ExchangePackageHeader(name, claimed.SourceInstance, null, "Подпись манифеста повреждена (не Base64).");
        }

        var key = sourceKeyFor(claimed.SourceInstance);
        if (key is null)
        {
            return new ExchangePackageHeader(
                name, claimed.SourceInstance, null,
                $"Источник «{claimed.SourceInstance}» не настроен: нет его открытого ключа подписи (Exchange:Import:Sources).");
        }

        if (!SealedSignature.Verify(bytes, signature, key))
        {
            return new ExchangePackageHeader(
                name, claimed.SourceInstance, null,
                $"Подпись манифеста не сходится с ключом источника «{claimed.SourceInstance}» {BackupKeys.FingerprintHex(key)}: пакет подписан другим ключом или изменён.");
        }

        return new ExchangePackageHeader(name, claimed.SourceInstance, Info(claimed, bytes), null);
    }

    /// <inheritdoc />
    public async Task<ExchangePackageVerification> VerifyAsync(
        string packageFolder, RSA sourceKey, string expectedRecipientFingerprint, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageFolder);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedRecipientFingerprint);
        var manifestPath = Path.Combine(packageFolder, SealedPackageManifest.FileName);
        var before = File.Exists(manifestPath)
            ? await File.ReadAllBytesAsync(manifestPath, cancellationToken).ConfigureAwait(false)
            : null;
        var check = await SealedPackageVerifier.VerifyAsync(
            packageFolder, sourceKey, SealedFormat.Exchange, expectedRecipientFingerprint, cancellationToken).ConfigureAwait(false);
        if (check.Manifest is null)
        {
            return new ExchangePackageVerification(null, check.Problems);
        }

        // SHA-256 манифеста (для акта приёма) — тех байтов, чья подпись сошлась: байты до и после проверки обязаны совпасть,
        // иначе манифест меняли во время проверки.
        var after = await File.ReadAllBytesAsync(manifestPath, cancellationToken).ConfigureAwait(false);
        var problems = check.Problems.ToList();
        if (before is null || !before.AsSpan().SequenceEqual(after))
        {
            problems.Add("Манифест изменился во время проверки — проверьте пакет заново.");
        }

        return new ExchangePackageVerification(Info(check.Manifest, after), problems);
    }

    /// <inheritdoc />
    public async Task<long> DecryptAsync(
        string packageFolder,
        string fileName,
        Stream output,
        RSA receivingKey,
        string? expectedSha256 = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageFolder);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(receivingKey);
        if (!SealedPackageWriter.IsSafeName(fileName))
        {
            // Имя — из манифеста; «../» и абсолютные пути за пределы каталога пакета не пускаются.
            throw new ArgumentException($"Недопустимое имя файла пакета «{fileName}».", nameof(fileName));
        }

        var path = Path.Combine(packageFolder, fileName.Replace('/', Path.DirectorySeparatorChar));
        await using var file = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var input = new HashingReadStream(file, hash);
        var length = await SealedFile.DecryptAsync(input, output, receivingKey, SealedFormat.Exchange, cancellationToken).ConfigureAwait(false);

        // Расшифровка дочитывает файл до конца (данные после последней порции — отказ), поэтому хеш — всего файла.
        if (expectedSha256 is not null
            && !string.Equals(Convert.ToHexString(hash.GetHashAndReset()), expectedSha256.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Файл пакета {fileName} изменён после проверки: SHA-256 не совпал с манифестом.");
        }

        return length;
    }

    private static RSA LoadPinnedPublicKey(string pemPath, string pinnedSha256, string fileWhat, string keyWhat)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pemPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(pinnedSha256);
        if (!File.Exists(pemPath))
        {
            throw new InvalidOperationException($"Файла {fileWhat} {pemPath} нет.");
        }

        RSA key;
        try
        {
            key = BackupKeys.LoadPublicKey(pemPath, KeyPurpose);
        }
        catch (CryptographicException exception)
        {
            // Заголовок PEM на месте, а внутри не ключ — для экрана это тот же «не ключ», не падение.
            throw new InvalidOperationException($"В файле {pemPath} нет открытого ключа RSA в формате PEM.", exception);
        }

        return Pinned(key, pinnedSha256, $"{keyWhat} {pemPath}");
    }

    private static RSA LoadPinnedPrivateKey(
        string pemPath, string pinnedSha256, ReadOnlySpan<char> password, string fileWhat, string passwordWhat, string keyWhat)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pemPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(pinnedSha256);
        if (!File.Exists(pemPath))
        {
            throw new InvalidOperationException($"Файла {fileWhat} {pemPath} нет.");
        }

        RSA key;
        try
        {
            key = BackupKeys.LoadPrivateKey(pemPath, password);
        }
        catch (Exception exception) when (exception is CryptographicException or ArgumentException)
        {
            // Неверный пароль и испорченный файл неразличимы — и не должны различаться для того, кто подбирает пароль.
            throw new InvalidOperationException($"{passwordWhat} неверен (или файл ключа повреждён).", exception);
        }

        return Pinned(key, pinnedSha256, keyWhat);
    }

    private static ExchangeManifestInfo Info(SealedPackageManifest manifest, byte[] bytes) => new(
        manifest.FormatVersion,
        manifest.Kind,
        manifest.SystemVersion,
        manifest.SourceInstance,
        manifest.Period,
        manifest.CreatedAt,
        manifest.CreatedByUserId,
        manifest.Classification,
        manifest.Marking,
        manifest.RecipientKeyFingerprint,
        manifest.SigningKeyFingerprint,
        manifest.Selection,
        manifest.Counts,
        [.. manifest.Files.Select(f => new ExchangePackageFileInfo(f.Name, f.Bytes, f.Sha256))],
        Convert.ToHexString(SHA256.HashData(bytes)));

    /// <inheritdoc />
    public IExchangePackageBuilder Create(string targetFolder, string folderName, RSA recipientKey) =>
        new Builder(SealedPackageWriter.Create(targetFolder, folderName, recipientKey, SealedFormat.Exchange), recipientKey);

    /// <inheritdoc />
    public int RemoveUnfinished(string targetFolder, string folderPrefix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetFolder);
        ArgumentException.ThrowIfNullOrWhiteSpace(folderPrefix);
        if (!Directory.Exists(targetFolder))
        {
            return 0;
        }

        var removed = 0;
        foreach (var partial in Directory.EnumerateDirectories(targetFolder, folderPrefix + "*" + SealedPackageWriter.PartialSuffix).ToList())
        {
            SealedPackageWriter.TryDelete(partial);
            if (!Directory.Exists(partial))
            {
                removed++;
            }
        }

        return removed;
    }

    // Ключ принимается, только если его отпечаток совпал с закреплённым в настройке (ТБ-083): подложенный на диск ключ
    // с тем же именем файла — отказ, ключ освобождается.
    private static RSA Pinned(RSA key, string pinnedSha256, string what)
    {
        var fingerprint = BackupKeys.FingerprintHex(key);
        if (!string.Equals(fingerprint, pinnedSha256.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            key.Dispose();
            throw new InvalidOperationException(
                $"{what} не совпал с закреплённым отпечатком: {fingerprint}, в настройке {pinnedSha256.Trim()}.");
        }

        return key;
    }

    private sealed class Builder(SealedPackageWriter writer, RSA recipientKey) : IExchangePackageBuilder
    {
        public string FolderName => Path.GetFileName(writer.FinalPath);

        public long BytesWritten => writer.BytesWritten;

        public async Task AddAsync(
            string name, Stream plaintext, Action<long>? progress = null, CancellationToken cancellationToken = default) =>
            await writer.AddAsync(name, plaintext, progress, cancellationToken).ConfigureAwait(false);

        public async Task<ExchangePackageSealed> CompleteAsync(
            ExchangeManifestContent content, RSA signingKey, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(content);
            ArgumentNullException.ThrowIfNull(signingKey);
            var recipient = BackupKeys.FingerprintHex(recipientKey);
            var signing = BackupKeys.FingerprintHex(signingKey);
            var manifest = await writer.CompleteAsync(
                files => new SealedPackageManifest(
                    SealedPackageManifest.CurrentFormat,
                    content.Kind,
                    content.SystemVersion,
                    content.SourceInstance,
                    content.Period,
                    content.CreatedAt,
                    content.CreatedByUserId,
                    content.Classification,
                    content.Marking,
                    recipient,
                    signing,
                    new Dictionary<string, string>(content.Selection, StringComparer.Ordinal),
                    new Dictionary<string, int>(content.Counts, StringComparer.Ordinal),
                    files),
                signingKey,
                cancellationToken).ConfigureAwait(false);

            return new ExchangePackageSealed(
                FolderName, manifest.Files.Sum(f => f.Bytes), manifest.Files.Count, recipient, signing, writer.ManifestSha256!);
        }

        public ValueTask DisposeAsync() => writer.DisposeAsync();
    }
}
