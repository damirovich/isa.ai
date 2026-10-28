using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ISC.AI.Backup;

/// <summary>
/// Манифест резервной копии (ТБ-082, ADR-0063): что в копии и как это проверить без закрытого ключа. Лежит рядом с
/// файлами копии открытым текстом — содержимого в нём нет: имена баз, объёмы, SHA-256 зашифрованных файлов, отпечаток
/// ключа.
/// </summary>
/// <param name="FormatVersion">Версия формата каталога копии.</param>
/// <param name="SystemVersion">Версия системы, сделавшей копию.</param>
/// <param name="CreatedAt">Когда сделана (с часовым поясом сервера).</param>
/// <param name="CreatedByUserId">Кто запустил (номер учётной записи); <see langword="null"/> — не из интерфейса.</param>
/// <param name="KeyFingerprint">Отпечаток открытого ключа, которым зашифрованы файлы.</param>
/// <param name="Databases">Базы данных в копии.</param>
/// <param name="Storages">Каталоги файлов в копии — по одному архиву на каталог (хранилище ядра и пакетов).</param>
/// <param name="Files">Файлы копии с размерами и SHA-256 зашифрованного содержимого.</param>
public sealed record BackupManifest(
    int FormatVersion,
    string SystemVersion,
    DateTimeOffset CreatedAt,
    int? CreatedByUserId,
    string KeyFingerprint,
    IReadOnlyList<BackupDatabaseEntry> Databases,
    IReadOnlyList<BackupStorageEntry> Storages,
    IReadOnlyList<BackupFileEntry> Files)
{
    /// <summary>Текущая версия формата.</summary>
    public const int CurrentFormat = 1;

    /// <summary>Имя файла манифеста в каталоге копии.</summary>
    public const string FileName = "manifest.json";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>Записывает манифест в каталог копии.</summary>
    public async Task SaveAsync(string folder, CancellationToken cancellationToken = default)
    {
        await using var stream = File.Create(Path.Combine(folder, FileName));
        await JsonSerializer.SerializeAsync(stream, this, Json, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Читает манифест из каталога копии.</summary>
    /// <exception cref="FileNotFoundException">Манифеста нет.</exception>
    /// <exception cref="InvalidDataException">Манифест не читается.</exception>
    public static async Task<BackupManifest> LoadAsync(string folder, CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(folder, FileName);
        await using var stream = File.OpenRead(path);
        try
        {
            return await JsonSerializer.DeserializeAsync<BackupManifest>(stream, Json, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("Манифест копии пуст.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"Манифест копии не читается: {exception.Message}", exception);
        }
    }
}

/// <summary>База данных в копии.</summary>
/// <param name="Name">Имя базы.</param>
/// <param name="Host">Сервер.</param>
/// <param name="Port">Порт.</param>
/// <param name="ServerVersion">Версия PostgreSQL.</param>
/// <param name="DumpToolVersion">Версия <c>pg_dump</c>.</param>
/// <param name="File">Файл копии базы.</param>
public sealed record BackupDatabaseEntry(string Name, string Host, int Port, string ServerVersion, string DumpToolVersion, string File);

/// <summary>Каталог файлов в копии.</summary>
/// <param name="Name">Имя каталога в копии (<c>core</c> — хранилище ядра, иначе — пакета).</param>
/// <param name="Root">Где каталог был на сервере в момент копии — туда восстанавливается по умолчанию.</param>
/// <param name="Files">Число файлов.</param>
/// <param name="Bytes">Объём файлов, байт.</param>
/// <param name="File">Файл архива каталога.</param>
public sealed record BackupStorageEntry(string Name, string Root, int Files, long Bytes, string File);

/// <summary>Файл копии.</summary>
/// <param name="Name">Имя файла в каталоге копии.</param>
/// <param name="Bytes">Размер, байт.</param>
/// <param name="Sha256">SHA-256 содержимого (зашифрованного), шестнадцатеричной строкой.</param>
public sealed record BackupFileEntry(string Name, long Bytes, string Sha256);
