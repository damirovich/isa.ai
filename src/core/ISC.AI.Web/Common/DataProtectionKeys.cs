using System.IO;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ISC.AI.Web.Common;

/// <summary>
/// Постоянный каталог ключей защиты данных ASP.NET Core — настройка <c>DataProtection:KeysPath</c>.
/// Этими ключами зашифрованы cookie сеанса входа (ТБ-014) и токены антифорджери форм.
/// </summary>
/// <remarks>
/// ЗАЧЕМ. Без явного каталога ASP.NET Core на Windows держит ключи в профиле пользователя под DPAPI, а на Linux —
/// в домашнем каталоге учётки службы; у системной учётки службы без домашнего каталога ключи живут только в памяти,
/// и каждый перезапуск хоста обрывает все сеансы и открытые формы. Права на каталог — только у учётки службы
/// (0700): на Linux ключи лежат на диске без шифрования, их защищает разграничение доступа ОС (ТБ-062).
/// Имя приложения закреплено: по умолчанию ASP.NET Core выводит его из пути к программе, и переход на новый каталог
/// релиза при обновлении (<c>/opt/isc-ai/releases/&lt;версия&gt;</c>) сделал бы недействительными все cookie.
/// Настройка не задана (дев-машина) — поведение ASP.NET Core по умолчанию, ничего не меняется.
/// </remarks>
public static class DataProtectionKeys
{
    /// <summary>Ключ настройки каталога ключей.</summary>
    public const string SettingKey = "DataProtection:KeysPath";

    /// <summary>Закреплённое имя приложения для изоляции ключей (не зависит от каталога установки).</summary>
    public const string ApplicationName = "ISC.AI";

    /// <summary>
    /// Подключает хранение ключей в каталоге из <see cref="SettingKey"/>; без настройки ничего не регистрирует.
    /// </summary>
    public static IServiceCollection AddPersistentDataProtectionKeys(
        this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var path = configuration[SettingKey];
        if (string.IsNullOrWhiteSpace(path))
        {
            return services;
        }

        services.AddDataProtection()
            .SetApplicationName(ApplicationName)
            .PersistKeysToFileSystem(new DirectoryInfo(path));
        return services;
    }
}
