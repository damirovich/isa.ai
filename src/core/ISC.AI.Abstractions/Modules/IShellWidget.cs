namespace ISC.AI.Abstractions.Modules;

/// <summary>Место оболочки, куда встраивается виджет профиля.</summary>
public enum ShellWidgetSlot
{
    /// <summary>Правая часть верхней панели — рядом с переключателем темы и выходом.</summary>
    AppBarRight = 0,

}

/// <summary>
/// Виджет оболочки: компонент, который ПРОФИЛЬ (а не хост) просит отрисовать в фиксированном месте
/// шапки. Нужен, чтобы доменные элементы вроде колокольчика уведомлений появлялись в общей оболочке,
/// а хост оставался доменно-нейтральным (ТС-003): он знает про «слот» и «тип компонента», но не про
/// уведомления, документы и инспекцию.
/// </summary>
/// <remarks>
/// Отличие от <see cref="IModule"/>: модуль — это СТРАНИЦА с маршрутом и пунктом меню, виджет —
/// постоянный элемент оболочки без собственного маршрута. Доступ виджет проверяет сам: оболочка
/// рисует его только аутентифицированному субъекту (ТБ-010), а всё, что тоньше, — дело компонента.
/// </remarks>
public interface IShellWidget
{
    /// <summary>Стабильный технический идентификатор (например, <c>"docflow-notifications"</c>).</summary>
    string Id { get; }

    /// <summary>Место встраивания.</summary>
    ShellWidgetSlot Slot { get; }

    /// <summary>Порядок внутри слота: меньше — левее (для <see cref="ShellWidgetSlot.AppBarRight"/>).</summary>
    int Order { get; }

    /// <summary>Тип Razor-компонента виджета; оболочка создаёт его через <c>DynamicComponent</c>.</summary>
    Type ComponentType { get; }

}

/// <summary>Готовая реализация <see cref="IShellWidget"/> для деклараций в манифесте профиля.</summary>
public sealed record ShellWidgetDescriptor(
    string Id,
    ShellWidgetSlot Slot,
    int Order,
    Type ComponentType) : IShellWidget;
