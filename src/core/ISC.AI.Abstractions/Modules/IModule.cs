namespace ISC.AI.Abstractions.Modules;

/// <summary>
/// Единица интерфейса профиля (модуль): маршрут, пункт меню, тип Razor-компонента
/// и требуемая политика доступа. Из набора модулей хост строит навигацию (ТС-007, ТО-прог-05).
/// </summary>
public interface IModule
{
    /// <summary>Стабильный технический идентификатор модуля (например, <c>"dashboard"</c>).</summary>
    string Id { get; }

    /// <summary>Маршрут модуля (например, <c>"/dashboard"</c>); основа пункта навигации.</summary>
    string Route { get; }

    /// <summary>Заголовок пункта меню для интерфейса (например, «Дашборд»).</summary>
    string MenuTitle { get; }

    /// <summary>Необязательный значок пункта меню (имя/константа иконки набора UI); <c>null</c> — без значка.</summary>
    string? MenuIcon { get; }

    /// <summary>
    /// Необязательная СЕКЦИЯ меню, в которую сгруппирован модуль (например, «Навигация», «Подразделения»);
    /// <c>null</c> — секция по умолчанию. Названия секций объявляет ПРОФИЛЬ — хост лишь группирует по ним
    /// и остаётся доменно-нейтральным (ТС-003, ТС-007).
    /// </summary>
    string? MenuGroup => null;

    /// <summary>
    /// Тип корневого Razor-компонента модуля. Хост использует его сборку для маршрутизации
    /// (страница объявляет собственный <c>@page</c>) и для построения навигации.
    /// </summary>
    Type ComponentType { get; }

    /// <summary>
    /// Имя требуемой политики авторизации. Доступ к маршруту разрешается только при её
    /// выполнении (роль/допуск/подразделение по решётке ТБ-002, ТБ-011). Анонимный доступ
    /// запрещён (ТБ-010); пустое значение недопустимо.
    /// </summary>
    string RequiredPolicy { get; }
}

/// <summary>
/// Готовая реализация <see cref="IModule"/> для деклараций модулей в манифесте профиля.
/// </summary>
/// <remarks><paramref name="MenuGroup"/> необязателен (по умолчанию <c>null</c>) — существующие декларации не ломаются.</remarks>
public sealed record ModuleDescriptor(
    string Id,
    string Route,
    string MenuTitle,
    string? MenuIcon,
    Type ComponentType,
    string RequiredPolicy,
    string? MenuGroup = null) : IModule;
