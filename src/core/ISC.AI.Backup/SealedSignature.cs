using System.Security.Cryptography;

namespace ISC.AI.Backup;

/// <summary>
/// Подпись манифеста ключом экземпляра-источника (ТБ-083, ADR-0068): RSA-PSS по SHA-256. Манифест перечисляет SHA-256
/// каждого файла пакета, поэтому подпись манифеста закрывает весь пакет: подменить, добавить или убрать файл незаметно
/// нельзя, а подделать подпись без закрытого ключа источника — тоже.
/// </summary>
public static class SealedSignature
{
    /// <summary>Подписывает данные закрытым ключом.</summary>
    public static byte[] Sign(ReadOnlySpan<byte> data, RSA privateKey)
    {
        ArgumentNullException.ThrowIfNull(privateKey);
        return privateKey.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
    }

    /// <summary>Проверяет подпись открытым ключом источника.</summary>
    public static bool Verify(ReadOnlySpan<byte> data, ReadOnlySpan<byte> signature, RSA publicKey)
    {
        ArgumentNullException.ThrowIfNull(publicKey);
        try
        {
            return publicKey.VerifyData(data, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
        }
        catch (CryptographicException)
        {
            // Подпись не той длины или мусор с носителя — не исключение для проверяющего, а «не сходится».
            return false;
        }
    }
}
