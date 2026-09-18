using System.Security.Cryptography;

namespace ISC.AI.Modules.Admin.Application.Features.Accounts;

/// <summary>Порождение временного пароля для выдачи администратором.</summary>
/// <remarks>
/// Криптостойкий генератор (не <c>Random</c>): предсказуемый временный пароль — это выданный доступ
/// (ТБ-043). Алфавит без похожих символов (0/O, 1/l/I): пароль диктуют голосом и переписывают от
/// руки, и ошибка прочтения здесь дороже пары битов энтропии — при 12 символах её с запасом хватает.
/// </remarks>
internal static class TemporaryPassword
{
    private const string Alphabet = "abcdefghijkmnpqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    /// <summary>Длина временного пароля.</summary>
    public const int Length = 12;

    /// <inheritdoc cref="TemporaryPassword" />
    public static string Generate() => RandomNumberGenerator.GetString(Alphabet, Length);
}
