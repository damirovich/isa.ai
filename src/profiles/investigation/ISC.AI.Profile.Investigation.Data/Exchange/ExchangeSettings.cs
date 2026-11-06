using System.Security.Cryptography;
using System.Text.RegularExpressions;
using ISC.AI.Abstractions.Exchange;
using ISC.AI.Profile.Investigation.Domain.Services;
using Microsoft.Extensions.Configuration;

namespace ISC.AI.Profile.Investigation.Data.Exchange;

/// <summary>
/// Настройки обмена между офисами (ADR-0068): номер экземпляра; для отправки — носитель обмена, ключ получателя с пином и
/// ключ подписи этого экземпляра; для приёма (v0.2) — носитель с пакетами, ключ приёма этого экземпляра и открытые ключи
/// подписи офисов-источников. Секретов здесь нет: закрытые ключи лежат файлами под паролем хранителя, пароль вводится при
/// формировании или приёме пакета.
/// </summary>
/// <remarks>
/// Ключи читает и сверяет с закреплёнными отпечатками ядро (<see cref="IExchangePackages"/>); здесь — только какие ключи
/// настройки не заданы, с именами ключей конфигурации для Администратора.
/// </remarks>
public sealed partial class ExchangeSettings
{
    /// <summary>Номер экземпляра (<c>Exchange:InstanceId</c>): «office-2».</summary>
    public string? InstanceId { get; init; }

    /// <summary>Носитель обмена — точка монтирования (<c>Exchange:TargetFolder</c>).</summary>
    public string? TargetFolder { get; init; }

    /// <summary>Открытый ключ получателя, PEM (<c>Exchange:Recipient:PublicKey:Path</c>).</summary>
    public string? RecipientKeyPath { get; init; }

    /// <summary>Пин отпечатка ключа получателя (<c>Exchange:Recipient:PublicKey:Sha256</c>).</summary>
    public string? RecipientKeySha256 { get; init; }

    /// <summary>Закрытый ключ подписи этого экземпляра, PEM под паролем (<c>Exchange:Signing:PrivateKey:Path</c>).</summary>
    public string? SigningKeyPath { get; init; }

    /// <summary>Пин отпечатка ключа подписи (<c>Exchange:Signing:PrivateKey:Sha256</c>) — его же сообщают получателю.</summary>
    public string? SigningKeySha256 { get; init; }

    /// <summary>Носитель с принимаемыми пакетами — точка монтирования после шлюза (<c>Exchange:Import:Folder</c>).</summary>
    public string? ImportFolder { get; init; }

    /// <summary>Закрытый ключ приёма этого экземпляра, PEM под паролем (<c>Exchange:Import:PrivateKey:Path</c>).</summary>
    public string? ReceivingKeyPath { get; init; }

    /// <summary>Пин отпечатка ключа приёма (<c>Exchange:Import:PrivateKey:Sha256</c>) — его сообщают офису-источнику.</summary>
    public string? ReceivingKeySha256 { get; init; }

    /// <summary>
    /// Офисы-источники (<c>Exchange:Import:Sources:&lt;номер&gt;:PublicKey:Path</c> и <c>:Sha256</c>): номер → открытый ключ
    /// подписи с пином. Пакет источника, которого здесь нет, не принимается.
    /// </summary>
    public IReadOnlyDictionary<string, ExchangeSourceKey> Sources { get; init; } =
        new Dictionary<string, ExchangeSourceKey>(StringComparer.Ordinal);

    /// <summary>Допуск таймкода по умолчанию — половина шага выборки кадров при 1 кадре/с.</summary>
    public const long DefaultFaceMatchMaxTimeOffsetMs = 500;

    /// <summary>Порог перекрытия рамок (IoU) по умолчанию.</summary>
    public const double DefaultFaceMatchMinOverlap = 0.5;

    /// <summary>Период фонового сопоставления по умолчанию, с.</summary>
    public const int DefaultFaceMatchPollSeconds = 60;

    /// <summary>
    /// Допуск таймкода при поиске того же лица в индексации получателя, мс (<c>Exchange:Import:FaceMatch:MaxTimeOffsetMs</c>,
    /// ADR-0068 v0.3): кадр получателя должен отстоять от кадра источника не дальше.
    /// </summary>
    public long FaceMatchMaxTimeOffsetMs { get; init; } = DefaultFaceMatchMaxTimeOffsetMs;

    /// <summary>Порог перекрытия рамок, IoU 0–1 (<c>Exchange:Import:FaceMatch:MinOverlap</c>).</summary>
    public double FaceMatchMinOverlap { get; init; } = DefaultFaceMatchMinOverlap;

    /// <summary>Период фонового сопоставления лиц после приёма, с (<c>Exchange:Import:FaceMatch:PollSeconds</c>).</summary>
    public int FaceMatchPollSeconds { get; init; } = DefaultFaceMatchPollSeconds;

    /// <summary>Настройки из конфигурации.</summary>
    public static ExchangeSettings Read(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return new ExchangeSettings
        {
            // Пределы держат настройку в разумном: допуск не шире 10 с (дальше «то же лицо» — уже другое), порог не ниже 0,1.
            FaceMatchMaxTimeOffsetMs = Math.Clamp(
                Number(configuration["Exchange:Import:FaceMatch:MaxTimeOffsetMs"]) is { } offset ? (long)offset : DefaultFaceMatchMaxTimeOffsetMs, 0, 10_000),
            FaceMatchMinOverlap = Math.Clamp(
                Number(configuration["Exchange:Import:FaceMatch:MinOverlap"]) ?? DefaultFaceMatchMinOverlap, 0.1, 1.0),
            FaceMatchPollSeconds = Math.Clamp(
                Number(configuration["Exchange:Import:FaceMatch:PollSeconds"]) is { } poll ? (int)poll : DefaultFaceMatchPollSeconds, 10, 3600),
            InstanceId = Value(configuration["Exchange:InstanceId"]),
            TargetFolder = Value(configuration["Exchange:TargetFolder"]),
            RecipientKeyPath = Value(configuration["Exchange:Recipient:PublicKey:Path"]),
            RecipientKeySha256 = Value(configuration["Exchange:Recipient:PublicKey:Sha256"]),
            SigningKeyPath = Value(configuration["Exchange:Signing:PrivateKey:Path"]),
            SigningKeySha256 = Value(configuration["Exchange:Signing:PrivateKey:Sha256"]),
            ImportFolder = Value(configuration["Exchange:Import:Folder"]),
            ReceivingKeyPath = Value(configuration["Exchange:Import:PrivateKey:Path"]),
            ReceivingKeySha256 = Value(configuration["Exchange:Import:PrivateKey:Sha256"]),
            Sources = configuration.GetSection("Exchange:Import:Sources").GetChildren()
                .ToDictionary(
                    s => s.Key.Trim(),
                    s => new ExchangeSourceKey(Value(s["PublicKey:Path"]), Value(s["PublicKey:Sha256"])),
                    StringComparer.Ordinal),
        };
    }

    /// <summary>Готовность к отправке: что настроено и что нет — текстом для Администратора.</summary>
    public ExchangeReadiness Check(IExchangePackages packages)
    {
        ArgumentNullException.ThrowIfNull(packages);
        var problems = new List<string>();
        CheckInstance(problems);

        if (TargetFolder is null)
        {
            problems.Add("Не задан носитель обмена (Exchange:TargetFolder) — точка монтирования носителя.");
        }
        else if (!Directory.Exists(TargetFolder))
        {
            problems.Add($"Носитель обмена {TargetFolder} не подключён: каталога нет.");
        }

        string? recipient = null;
        try
        {
            using var key = LoadRecipientKey(packages);
            recipient = packages.Fingerprint(key);
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            problems.Add(exception.Message);
        }

        if (SigningKeyPath is null)
        {
            problems.Add("Не задан ключ подписи этого экземпляра (Exchange:Signing:PrivateKey:Path).");
        }
        else if (!File.Exists(SigningKeyPath))
        {
            problems.Add($"Файла ключа подписи {SigningKeyPath} нет.");
        }

        if (SigningKeySha256 is null)
        {
            problems.Add("Не задан отпечаток ключа подписи (Exchange:Signing:PrivateKey:Sha256) — его печатает keygen.");
        }

        return new ExchangeReadiness(InstanceId, TargetFolder, recipient, SigningKeySha256?.ToUpperInvariant(), problems);
    }

    /// <summary>
    /// Готовность к приёму (ADR-0068 v0.2): номер экземпляра, носитель, ключ приёма (сам ключ под паролем — здесь только
    /// пин; пароль и совпадение отпечатка проверяются при запуске), офисы-источники с проверкой их ключей по пинам.
    /// </summary>
    public ExchangeImportReadiness CheckImport(IExchangePackages packages)
    {
        ArgumentNullException.ThrowIfNull(packages);
        var problems = new List<string>();
        CheckInstance(problems);
        if (ImportFolder is null)
        {
            problems.Add("Не задан носитель с пакетами (Exchange:Import:Folder) — точка монтирования носителя после шлюза.");
        }
        else if (!Directory.Exists(ImportFolder))
        {
            problems.Add($"Носитель с пакетами {ImportFolder} не подключён: каталога нет.");
        }

        if (ReceivingKeyPath is null)
        {
            problems.Add("Не задан ключ приёма этого экземпляра (Exchange:Import:PrivateKey:Path) — keygen --purpose exchange-recipient.");
        }
        else if (!File.Exists(ReceivingKeyPath))
        {
            problems.Add($"Файла ключа приёма {ReceivingKeyPath} нет.");
        }

        if (ReceivingKeySha256 is null)
        {
            problems.Add("Не задан отпечаток ключа приёма (Exchange:Import:PrivateKey:Sha256) — его печатает keygen.");
        }

        var sources = new List<ExchangeSourceInfo>();
        if (Sources.Count == 0)
        {
            problems.Add("Не задан ни один офис-источник (Exchange:Import:Sources:<номер>:PublicKey:Path и :Sha256).");
        }

        foreach (var (instance, _) in Sources.OrderBy(s => s.Key, StringComparer.Ordinal))
        {
            string? fingerprint = null;
            string? problem = null;
            if (!InstanceIdPattern().IsMatch(instance))
            {
                problem = $"Номер источника «{instance}»: допустимы строчная латиница, цифры и дефис, до 32 знаков.";
            }
            else
            {
                try
                {
                    using var key = LoadSourceKey(packages, instance)
                        ?? throw new InvalidOperationException($"Источник «{instance}»: не задан путь или отпечаток ключа подписи.");
                    fingerprint = packages.Fingerprint(key);
                }
                catch (Exception exception) when (exception is InvalidOperationException or IOException or UnauthorizedAccessException)
                {
                    problem = exception.Message;
                }
            }

            if (problem is not null)
            {
                problems.Add(problem);
            }

            sources.Add(new ExchangeSourceInfo(instance, fingerprint, problem));
        }

        return new ExchangeImportReadiness(InstanceId, ImportFolder, ReceivingKeySha256?.ToUpperInvariant(), sources, problems);
    }

    /// <summary>
    /// Открытый ключ подписи источника с проверкой пина; <see langword="null"/> — источник не настроен (пакет такого
    /// источника не принимается).
    /// </summary>
    /// <exception cref="InvalidOperationException">Ключ не читается или не совпал с пином.</exception>
    public RSA? LoadSourceKey(IExchangePackages packages, string instance)
    {
        ArgumentNullException.ThrowIfNull(packages);
        return Sources.TryGetValue(instance, out var source) && source.Path is { } path && source.Sha256 is { } pin
            ? packages.LoadSourceKey(path, pin)
            : null;
    }

    /// <summary>Закрытый ключ приёма по паролю хранителя с проверкой пина. Пароль не сохраняется.</summary>
    /// <exception cref="InvalidOperationException">Ключ не настроен, пароль неверен или ключ не тот, что закреплён.</exception>
    public RSA LoadReceivingKey(IExchangePackages packages, ReadOnlySpan<char> password)
    {
        ArgumentNullException.ThrowIfNull(packages);
        if (ReceivingKeyPath is null || ReceivingKeySha256 is null)
        {
            throw new InvalidOperationException("Ключ приёма этого экземпляра не настроен (Exchange:Import:PrivateKey:Path и :Sha256).");
        }

        return packages.LoadReceivingKey(ReceivingKeyPath, ReceivingKeySha256, password);
    }

    /// <summary>Открытый ключ получателя с проверкой пина: подменённый ключ пакет не получит (ТИ-004, ТБ-083).</summary>
    /// <exception cref="InvalidOperationException">Ключ не настроен, не читается или не совпал с пином.</exception>
    public RSA LoadRecipientKey(IExchangePackages packages)
    {
        ArgumentNullException.ThrowIfNull(packages);
        if (RecipientKeyPath is null || RecipientKeySha256 is null)
        {
            throw new InvalidOperationException(
                "Не задан ключ получателя (Exchange:Recipient:PublicKey:Path и :Sha256) — открытый ключ офиса, которому идёт пакет.");
        }

        return packages.LoadRecipientKey(RecipientKeyPath, RecipientKeySha256);
    }

    /// <summary>
    /// Закрытый ключ подписи по паролю хранителя с проверкой пина. Пароль не сохраняется.
    /// </summary>
    /// <exception cref="InvalidOperationException">Ключ не настроен, пароль неверен или ключ не тот, что закреплён.</exception>
    public RSA LoadSigningKey(IExchangePackages packages, ReadOnlySpan<char> password)
    {
        ArgumentNullException.ThrowIfNull(packages);
        if (SigningKeyPath is null || SigningKeySha256 is null)
        {
            throw new InvalidOperationException("Ключ подписи этого экземпляра не настроен (Exchange:Signing:PrivateKey:Path и :Sha256).");
        }

        return packages.LoadSigningKey(SigningKeyPath, SigningKeySha256, password);
    }

    /// <summary>Номер экземпляра годен: строчная латиница, цифры и дефис, до 32 знаков.</summary>
    public static bool IsValidInstanceId(string? instance) => instance is not null && InstanceIdPattern().IsMatch(instance);

    private void CheckInstance(List<string> problems)
    {
        if (InstanceId is null)
        {
            problems.Add("Не задан номер экземпляра (Exchange:InstanceId), например office-2.");
        }
        else if (!InstanceIdPattern().IsMatch(InstanceId))
        {
            problems.Add($"Номер экземпляра «{InstanceId}»: допустимы строчная латиница, цифры и дефис, до 32 знаков.");
        }
    }

    private static string? Value(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    // Число из конфигурации — с точкой, независимо от культуры сервера; нечитаемое — умолчание.
    private static double? Number(string? text) =>
        double.TryParse(Value(text), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value)
        && double.IsFinite(value)
            ? value
            : null;

    [GeneratedRegex("^[a-z0-9-]{1,32}$")]
    private static partial Regex InstanceIdPattern();
}

/// <summary>Открытый ключ подписи офиса-источника в настройке получателя: путь к PEM и пин отпечатка.</summary>
/// <param name="Path">Путь к PEM.</param>
/// <param name="Sha256">Пин отпечатка.</param>
public sealed record ExchangeSourceKey(string? Path, string? Sha256);
