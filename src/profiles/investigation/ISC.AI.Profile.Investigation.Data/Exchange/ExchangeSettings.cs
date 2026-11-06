using System.Security.Cryptography;
using System.Text.RegularExpressions;
using ISC.AI.Abstractions.Exchange;
using ISC.AI.Profile.Investigation.Domain.Services;
using Microsoft.Extensions.Configuration;

namespace ISC.AI.Profile.Investigation.Data.Exchange;

/// <summary>
/// Настройки обмена между офисами (ADR-0068): номер экземпляра, носитель обмена, ключ получателя с пином и ключ подписи
/// этого экземпляра. Секретов здесь нет: закрытый ключ подписи лежит файлом под паролем хранителя, пароль вводится при
/// формировании пакета.
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

    /// <summary>Настройки из конфигурации.</summary>
    public static ExchangeSettings Read(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return new ExchangeSettings
        {
            InstanceId = Value(configuration["Exchange:InstanceId"]),
            TargetFolder = Value(configuration["Exchange:TargetFolder"]),
            RecipientKeyPath = Value(configuration["Exchange:Recipient:PublicKey:Path"]),
            RecipientKeySha256 = Value(configuration["Exchange:Recipient:PublicKey:Sha256"]),
            SigningKeyPath = Value(configuration["Exchange:Signing:PrivateKey:Path"]),
            SigningKeySha256 = Value(configuration["Exchange:Signing:PrivateKey:Sha256"]),
        };
    }

    /// <summary>Готовность: что настроено и что нет — текстом для Администратора.</summary>
    public ExchangeReadiness Check(IExchangePackages packages)
    {
        ArgumentNullException.ThrowIfNull(packages);
        var problems = new List<string>();
        if (InstanceId is null)
        {
            problems.Add("Не задан номер экземпляра (Exchange:InstanceId), например office-2.");
        }
        else if (!InstanceIdPattern().IsMatch(InstanceId))
        {
            problems.Add($"Номер экземпляра «{InstanceId}»: допустимы строчная латиница, цифры и дефис, до 32 знаков.");
        }

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

    private static string? Value(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    [GeneratedRegex("^[a-z0-9-]{1,32}$")]
    private static partial Regex InstanceIdPattern();
}
