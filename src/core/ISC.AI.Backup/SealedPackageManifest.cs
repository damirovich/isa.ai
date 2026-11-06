using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ISC.AI.Backup;

/// <summary>
/// Манифест запечатанного пакета (ТБ-083, ТО-инф-14, ADR-0068): что за пакет, откуда, за какой период, для кого
/// зашифрован, чем подписан, сколько в нём записей каждого вида и какие в нём файлы с их SHA-256. Не шифруется — содержимого
/// в нём нет; подписывается ключом источника (<c>manifest.sig</c>), и подпись закрывает весь пакет через хеши файлов.
/// </summary>
/// <param name="FormatVersion">Версия формата пакета.</param>
/// <param name="Kind">Вид пакета: «isc-exchange» — обмен между офисами.</param>
/// <param name="SystemVersion">Версия системы-источника.</param>
/// <param name="SourceInstance">Номер экземпляра-источника (<c>Exchange:InstanceId</c>).</param>
/// <param name="Period">Период пакета: «гггг-ММ».</param>
/// <param name="CreatedAt">Когда сформирован.</param>
/// <param name="CreatedByUserId">Кто сформировал (номер учётной записи источника).</param>
/// <param name="Classification">Маркировка: наибольший гриф содержимого.</param>
/// <param name="Marking">Маркировка словами — для человека на шлюзе и у получателя.</param>
/// <param name="RecipientKeyFingerprint">Отпечаток ключа получателя: им зашифрован каждый файл.</param>
/// <param name="SigningKeyFingerprint">Отпечаток ключа подписи источника.</param>
/// <param name="Selection">Отбор, по которому собран пакет (для акта и для человека).</param>
/// <param name="Counts">Счётчики записей по видам — для сверки при импорте (ЦС-08).</param>
/// <param name="Files">Файлы пакета: имя, объём и SHA-256 зашифрованного содержимого.</param>
public sealed record SealedPackageManifest(
    int FormatVersion,
    string Kind,
    string SystemVersion,
    string SourceInstance,
    string Period,
    DateTimeOffset CreatedAt,
    int? CreatedByUserId,
    short Classification,
    string Marking,
    string RecipientKeyFingerprint,
    string SigningKeyFingerprint,
    IReadOnlyDictionary<string, string> Selection,
    IReadOnlyDictionary<string, int> Counts,
    IReadOnlyList<SealedPackageFile> Files)
{
    /// <summary>Текущая версия формата.</summary>
    public const int CurrentFormat = 1;

    /// <summary>Имя файла манифеста.</summary>
    public const string FileName = "manifest.json";

    /// <summary>Имя файла подписи манифеста.</summary>
    public const string SignatureFileName = "manifest.sig";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>Байты манифеста — именно их подписывают и проверяют (а не результат повторной сериализации).</summary>
    public byte[] ToBytes() => JsonSerializer.SerializeToUtf8Bytes(this, Json);

    /// <summary>Манифест из байтов файла.</summary>
    /// <exception cref="InvalidDataException">Байты — не манифест пакета.</exception>
    public static SealedPackageManifest FromBytes(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        try
        {
            return JsonSerializer.Deserialize<SealedPackageManifest>(bytes, Json)
                ?? throw new InvalidDataException("Манифест пакета пуст.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"Манифест пакета не читается: {exception.Message}", exception);
        }
    }
}

/// <summary>Файл пакета в манифесте.</summary>
/// <param name="Name">Путь внутри пакета через «/» (например «media/000001.enc»).</param>
/// <param name="Bytes">Объём зашифрованного файла.</param>
/// <param name="Sha256">SHA-256 зашифрованного файла (прописные шестнадцатеричные).</param>
public sealed record SealedPackageFile(string Name, long Bytes, string Sha256);
