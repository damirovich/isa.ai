using System.Text;

namespace ISC.AI.Backup.Restore;

/// <summary>Ввод и вывод программы восстановления — подменяется в тестах сценарием ответов.</summary>
public interface IRestoreConsole
{
    /// <summary>Строка вывода.</summary>
    void WriteLine(string text = "");

    /// <summary>Вопрос и ответ (видимый ввод); <see langword="null"/> — ввод закончился.</summary>
    string? Ask(string prompt);

    /// <summary>Вопрос и скрытый ответ (пароль): символы не отображаются и в историю не попадают.</summary>
    string? AskSecret(string prompt);
}

/// <summary>Консоль сервера: пароль вводится без отображения.</summary>
public sealed class SystemRestoreConsole : IRestoreConsole
{
    /// <inheritdoc />
    public void WriteLine(string text = "") => Console.WriteLine(text);

    /// <inheritdoc />
    public string? Ask(string prompt)
    {
        Console.Write(prompt);
        return Console.ReadLine();
    }

    /// <inheritdoc />
    public string? AskSecret(string prompt)
    {
        Console.Write(prompt);
        if (Console.IsInputRedirected)
        {
            return Console.ReadLine();
        }

        var secret = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                return secret.ToString();
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (secret.Length > 0)
                {
                    secret.Length--;
                }

                continue;
            }

            if (!char.IsControl(key.KeyChar))
            {
                secret.Append(key.KeyChar);
            }
        }
    }
}
