namespace ISC.AI.Abstractions.Backup;

/// <summary>
/// Каталог файлов, который входит в резервную копию (ТФ-АДМ-04, ТБ-082, ADR-0063). Ядро регистрирует своё хранилище
/// (<see cref="CoreName"/>), пакет или профиль со своим файловым хранилищем — своё (<c>services.AddSingleton(new
/// BackupFileRoot(...))</c>): копия берёт все зарегистрированные каталоги, ядро о пакетах не знает.
/// </summary>
/// <remarks>
/// ИНВАРИАНТ (ТБ-082 — полная копия): каталог, где пакет хранит файлы, на которые ссылаются строки его базы, обязан быть
/// зарегистрирован — иначе после восстановления строки останутся без файлов. Временные каталоги не регистрируются.
/// </remarks>
/// <param name="Name">
/// Имя в копии: латиница в нижнем регистре, цифры и «-» (входит в имя файла копии и в ключ <c>--files-&lt;имя&gt;</c>
/// программы восстановления); у каждого каталога своё.
/// </param>
/// <param name="Path">Полный путь каталога на сервере приложений.</param>
public sealed record BackupFileRoot(string Name, string Path)
{
    /// <summary>Имя файлового хранилища ядра (<c>Storage:BasePath</c>); в нём же — отметки о восстановлении.</summary>
    public const string CoreName = "core";

    /// <summary>Имя годится для копии: 1–32 символа, латиница в нижнем регистре, цифры и «-», не с «-».</summary>
    public static bool IsValidName(string? name) =>
        !string.IsNullOrEmpty(name) && name.Length <= 32 && name[0] != '-'
        && name.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-');
}
