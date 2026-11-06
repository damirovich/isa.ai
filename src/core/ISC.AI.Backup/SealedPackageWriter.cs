using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace ISC.AI.Backup;

/// <summary>
/// Запись запечатанного пакета на носитель (ТБ-083, ADR-0068): каталог с окончанием «.partial», в него — зашифрованные для
/// получателя файлы (<see cref="SealedFile"/>) с подсчётом SHA-256 по ходу записи; последним шагом — манифест, его подпись
/// ключом источника и переименование каталога. Оборванный пакет за готовый не сойдёт: незавершённый каталог удаляется при
/// освобождении писателя.
/// </summary>
public sealed partial class SealedPackageWriter : IAsyncDisposable
{
    /// <summary>Окончание каталога, пока пакет пишется.</summary>
    public const string PartialSuffix = ".partial";

    private readonly RSA _recipientKey;
    private readonly SealedFormat _format;
    private readonly List<SealedPackageFile> _files = [];
    private readonly HashSet<string> _names = new(StringComparer.OrdinalIgnoreCase);
    private bool _completed;

    private SealedPackageWriter(string final, string partial, RSA recipientKey, SealedFormat format)
    {
        FinalPath = final;
        PartialPath = partial;
        _recipientKey = recipientKey;
        _format = format;
    }

    /// <summary>Готовый каталог пакета (после <see cref="CompleteAsync"/>).</summary>
    public string FinalPath { get; }

    /// <summary>Каталог, в который пакет пишется.</summary>
    public string PartialPath { get; }

    /// <summary>Сколько байт уже записано на носитель.</summary>
    public long BytesWritten { get; private set; }

    /// <summary>SHA-256 байтов манифеста, как они записаны на носитель (HEX), — после <see cref="CompleteAsync"/>.</summary>
    public string? ManifestSha256 { get; private set; }

    /// <summary>Файлы, уже записанные в пакет.</summary>
    public IReadOnlyList<SealedPackageFile> Files => _files;

    /// <summary>Начинает пакет: создаёт каталог «.partial».</summary>
    /// <exception cref="IOException">Пакет с таким именем на носителе уже есть.</exception>
    public static SealedPackageWriter Create(string targetFolder, string folderName, RSA recipientKey, SealedFormat format)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetFolder);
        ArgumentException.ThrowIfNullOrWhiteSpace(folderName);
        ArgumentNullException.ThrowIfNull(recipientKey);
        ArgumentNullException.ThrowIfNull(format);
        if (!SafeSegment().IsMatch(folderName))
        {
            throw new ArgumentException($"Имя каталога пакета «{folderName}»: допустимы латиница, цифры, точка, дефис, подчёркивание.", nameof(folderName));
        }

        var final = Path.Combine(targetFolder, folderName);
        var partial = final + PartialSuffix;
        if (Directory.Exists(final) || Directory.Exists(partial))
        {
            throw new IOException($"На носителе уже есть пакет {folderName}.");
        }

        Directory.CreateDirectory(partial);
        return new SealedPackageWriter(final, partial, recipientKey, format);
    }

    /// <summary>Шифрует поток в файл пакета <paramref name="name"/> (путь через «/»); возвращает запись манифеста.</summary>
    public async Task<SealedPackageFile> AddAsync(
        string name, Stream plaintext, Action<long>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        ObjectDisposedException.ThrowIf(_completed, this);
        if (!IsSafeName(name) || !_names.Add(name))
        {
            throw new ArgumentException($"Имя файла пакета «{name}» недопустимо или уже занято.", nameof(name));
        }

        var path = Path.Combine(PartialPath, name.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var before = BytesWritten;
        await using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.Asynchronous))
        using (var hashing = new HashingWriteStream(file, written => progress?.Invoke(before + written)))
        {
            await SealedFile.EncryptAsync(plaintext, hashing, _recipientKey, _format, cancellationToken).ConfigureAwait(false);
            await hashing.FlushAsync(cancellationToken).ConfigureAwait(false);
            var entry = new SealedPackageFile(name, hashing.Written, hashing.HashHex());
            _files.Add(entry);
            BytesWritten += hashing.Written;
            return entry;
        }
    }

    /// <summary>
    /// Завершает пакет: манифест (<paramref name="manifestFor"/> получает список файлов), подпись ключом источника,
    /// переименование каталога. Возвращает манифест в том виде, в каком он подписан.
    /// </summary>
    public async Task<SealedPackageManifest> CompleteAsync(
        Func<IReadOnlyList<SealedPackageFile>, SealedPackageManifest> manifestFor, RSA signingKey, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifestFor);
        ArgumentNullException.ThrowIfNull(signingKey);
        ObjectDisposedException.ThrowIf(_completed, this);

        var manifest = manifestFor(_files.ToList());
        var bytes = manifest.ToBytes();
        var manifestSha256 = Convert.ToHexString(SHA256.HashData(bytes));
        await File.WriteAllBytesAsync(Path.Combine(PartialPath, SealedPackageManifest.FileName), bytes, cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(
            Path.Combine(PartialPath, SealedPackageManifest.SignatureFileName),
            Convert.ToBase64String(SealedSignature.Sign(bytes, signingKey)) + "\n",
            cancellationToken).ConfigureAwait(false);

        Directory.Move(PartialPath, FinalPath);
        ManifestSha256 = manifestSha256;
        _completed = true;
        return manifest;
    }

    /// <summary>Незавершённый пакет удаляется с носителя.</summary>
    public ValueTask DisposeAsync()
    {
        if (!_completed)
        {
            TryDelete(PartialPath);
            _completed = true;
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>Допустимо ли имя файла пакета: относительный путь через «/» из латиницы, цифр, точки, дефиса.</summary>
    public static bool IsSafeName(string? name) =>
        !string.IsNullOrEmpty(name)
        && name.Split('/').All(part => part.Length > 0 && part != "." && part != ".." && SafeSegment().IsMatch(part))
        && name is not (SealedPackageManifest.FileName or SealedPackageManifest.SignatureFileName);

    /// <summary>Удаляет каталог, не роняя вызывающего: сбой уборки не важнее исходной ошибки.</summary>
    public static void TryDelete(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [GeneratedRegex("^[A-Za-z0-9._-]+$")]
    private static partial Regex SafeSegment();
}
