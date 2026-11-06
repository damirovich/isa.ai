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
    public RSA LoadRecipientKey(string pemPath, string pinnedSha256)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pemPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(pinnedSha256);
        if (!File.Exists(pemPath))
        {
            throw new InvalidOperationException($"Файла ключа получателя {pemPath} нет.");
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

        return Pinned(key, pinnedSha256, $"Ключ получателя {pemPath}");
    }

    /// <inheritdoc />
    public RSA LoadSigningKey(string pemPath, string pinnedSha256, ReadOnlySpan<char> password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pemPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(pinnedSha256);
        if (!File.Exists(pemPath))
        {
            throw new InvalidOperationException($"Файла ключа подписи {pemPath} нет.");
        }

        RSA key;
        try
        {
            key = BackupKeys.LoadPrivateKey(pemPath, password);
        }
        catch (Exception exception) when (exception is CryptographicException or ArgumentException)
        {
            // Неверный пароль и испорченный файл неразличимы — и не должны различаться для того, кто подбирает пароль.
            throw new InvalidOperationException("Пароль ключа подписи неверен (или файл ключа повреждён).", exception);
        }

        return Pinned(key, pinnedSha256, "Ключ подписи");
    }

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
