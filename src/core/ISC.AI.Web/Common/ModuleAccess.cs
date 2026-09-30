using ISC.AI.Abstractions.Modules;
using ISC.AI.Abstractions.Profiles;

namespace ISC.AI.Web.Common;

/// <summary>Порт по умолчанию: профиль без ролей показывает все свои разделы.</summary>
public sealed class AllModulesVisible : IModuleVisibility
{
    /// <inheritdoc />
    public Task<IReadOnlySet<string>> GetVisibleModuleIdsAsync(
        IReadOnlyCollection<IModule> modules, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(modules);
        return Task.FromResult<IReadOnlySet<string>>(modules.Select(m => m.Id).ToHashSet(StringComparer.Ordinal));
    }
}

/// <summary>
/// Видимость разделов для меню и оболочки (ТС-007, ТП-004): один раз на соединение спрашивает профиль
/// (<see cref="IModuleVisibility"/>) и отвечает, виден ли пункт меню и не открыт ли по прямой ссылке скрытый раздел.
/// </summary>
/// <remarks>
/// Правило интерфейсное — права на действия проверяет сервер (ТБ-012). Роль, изменённая Администратором, в меню
/// видна после перезагрузки страницы: набор кэшируется на время соединения, чтобы меню не опрашивало роли на
/// каждом переходе. Сбой порта — показываем всё: пустое меню отрезало бы человека от работы, а доступ к
/// действиям всё равно решает сервер.
/// </remarks>
public sealed class ModuleAccess(IProfile profile, IModuleVisibility visibility, ILogger<ModuleAccess> logger)
{
    private Task<IReadOnlySet<string>>? _visible;

    /// <summary>Видимые текущему пользователю модули профиля.</summary>
    public Task<IReadOnlySet<string>> GetVisibleIdsAsync() => _visible ??= LoadAsync();

    private async Task<IReadOnlySet<string>> LoadAsync()
    {
        try
        {
            return await visibility.GetVisibleModuleIdsAsync(profile.Modules);
        }
#pragma warning disable CA1031 // Сбой порта не должен ронять оболочку — см. remarks класса.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            logger.LogWarning(ex, "Видимость разделов меню не определена — показываются все разделы");
            return profile.Modules.Select(m => m.Id).ToHashSet(StringComparer.Ordinal);
        }
    }

    /// <summary>
    /// Скрытый раздел, которому принадлежит путь (маршрут модуля или вложенный под ним: <c>/admin/roles</c>,
    /// <c>/admin/roles/5</c>); не скрыт — <see langword="null"/>.
    /// </summary>
    /// <param name="relativePath">Путь относительно корня приложения, без ведущей «/» (как отдаёт NavigationManager).</param>
    /// <param name="modules">Модули профиля.</param>
    /// <param name="visibleIds">Видимые модули.</param>
    public static IModule? HiddenModuleFor(string relativePath, IEnumerable<IModule> modules, IReadOnlySet<string> visibleIds)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        ArgumentNullException.ThrowIfNull(modules);
        ArgumentNullException.ThrowIfNull(visibleIds);

        var path = "/" + relativePath.Split('?', '#')[0].Trim('/');
        return modules
            .Where(m => !visibleIds.Contains(m.Id))
            .FirstOrDefault(m => IsUnder(path, m.Route));
    }

    private static bool IsUnder(string path, string route)
    {
        var root = "/" + route.Trim('/');
        return root.Length > 1
            && (path.Equals(root, StringComparison.OrdinalIgnoreCase)
                || path.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase));
    }
}
