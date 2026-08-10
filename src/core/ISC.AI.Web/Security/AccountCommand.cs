using System.Security.Cryptography;
using ISC.AI.Abstractions.Security;
using Microsoft.Extensions.DependencyInjection;

namespace ISC.AI.Web.Security;

/// <summary>
/// Служебная команда хоста: завести учётную запись для входа либо сбросить ей пароль (Э4-35 §6.5).
/// Запускается вместо сервера: <c>dotnet run --project src/core/ISC.AI.Web -- create-account &lt;логин&gt; [ФИО]</c>.
/// </summary>
/// <remarks>
/// ЕДИНСТВЕННЫЙ вход «снаружи» в систему без учёток: чистый контур, потерянный пароль администратора,
/// пустая база после развёртывания. Учётки по умолчанию в системе НЕТ и быть не должно —
/// предустановленный пароль это открытая дверь на весь срок эксплуатации (см. комментарий в Program.cs
/// о том, почему не засев).
///
/// РОЛЬ КОМАНДА НЕ ВЫДАЁТ — только учётку. Администратора человек назначает себе уже в интерфейсе:
/// пока в системе нет ни одного Администратора, страница ролей открыта любому вошедшему (§6.4.1).
/// Так право распоряжаться ролями остаётся внутри системы и попадает в неизменяемый журнал, а не
/// раздаётся консольной командой в обход аудита.
///
/// ПАРОЛЬ НЕ ПРИНИМАЕТСЯ АРГУМЕНТОМ: он попал бы в историю команд оболочки и в список процессов
/// (ТБ-043). Команда порождает его криптостойко и печатает ОДИН раз — в базе только хеш.
/// </remarks>
public static class AccountCommand
{
    private const string Verb = "create-account";

    // Алфавит без похожих символов (0/O, 1/l/I): пароль диктуют голосом и переписывают от руки,
    // ошибка прочтения дороже пары битов энтропии — при 14 символах её с запасом хватает.
    private const string Alphabet = "abcdefghijkmnpqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    private const int PasswordLength = 14;

    /// <summary>Запрошена ли служебная команда (иначе хост поднимает сервер как обычно).</summary>
    public static bool Matches(string[] args) =>
        args.Length > 0 && string.Equals(args[0], Verb, StringComparison.OrdinalIgnoreCase);

    /// <summary>Выполняет команду. Возвращает код возврата процесса.</summary>
    public static async Task<int> RunAsync(IServiceProvider services, string[] args)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(args);

        if (args.Length < 2 || string.IsNullOrWhiteSpace(args[1]))
        {
            await Console.Error.WriteLineAsync(
                $"Укажите имя входа: dotnet run --project src/core/ISC.AI.Web -- {Verb} <логин> [ФИО]");
            return 1;
        }

        var login = args[1].Trim();
        var displayName = args.Length > 2 ? string.Join(' ', args[2..]).Trim() : null;

        // Собственный scope: команда живёт вне запроса, а хранилище учёток зарегистрировано как scoped.
        using var scope = services.CreateScope();
        var accounts = scope.ServiceProvider.GetRequiredService<IUserAccountStore>();

        var existing = (await accounts.ListAsync())
            .FirstOrDefault(a => string.Equals(a.UserName, login, StringComparison.OrdinalIgnoreCase));

        var password = RandomNumberGenerator.GetString(Alphabet, PasswordLength);

        if (existing is null)
        {
            if (await accounts.CreateAsync(login, displayName, password) is null)
            {
                await Console.Error.WriteLineAsync("Имя входа уже занято.");
                return 1;
            }

            Console.WriteLine($"Создана учётная запись «{login}».");
        }
        else
        {
            // Существующей — СБРОС: команда обязана выручать и тогда, когда администратор потерял
            // пароль, а не только на пустой базе.
            if (!await accounts.ResetPasswordAsync(existing.UserId, password))
            {
                await Console.Error.WriteLineAsync("Не удалось сбросить пароль.");
                return 1;
            }

            if (!existing.IsActive)
            {
                await accounts.SetActiveAsync(existing.UserId, isActive: true);
                Console.WriteLine("Учётная запись была отключена — включена.");
            }

            Console.WriteLine($"Пароль учётной записи «{login}» сброшен, сессии завершены.");
        }

        Console.WriteLine();
        Console.WriteLine($"  Временный пароль: {password}");
        Console.WriteLine();
        Console.WriteLine("Показан один раз — в базе только хеш. При первом входе смените пароль.");
        Console.WriteLine("Дальше: войдите → «Роли пользователей» → назначьте себе Администратора →");
        Console.WriteLine("«Допуски пользователей» → выдайте себе гриф и подразделения.");
        return 0;
    }
}
