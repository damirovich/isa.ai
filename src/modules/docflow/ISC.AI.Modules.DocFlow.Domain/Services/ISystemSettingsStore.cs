namespace ISC.AI.Modules.DocFlow.Domain.Services;

/// <summary>Значения системных настроек модуля (ТЗ СКИД §9).</summary>
/// <param name="NotificationHorizonDays">
/// За сколько дней до срока присылать уведомление «срок приближается» (разд. 5 ТЗ).
/// </param>
public sealed record DocFlowSettings(int NotificationHorizonDays)
{
    /// <summary>Наименьший допустимый горизонт (в днях).</summary>
    public const int MinHorizonDays = 1;

    /// <summary>
    /// Наибольший допустимый горизонт (в днях), перенос границы СКИД.
    /// </summary>
    /// <remarks>
    /// Потолок не декоративный: горизонт задаёт окно отбора назначений в фоновой проверке сроков.
    /// Значение вроде 3650 превратило бы ежечасный тик в рассылку уведомлений почти по всему корпусу.
    /// </remarks>
    public const int MaxHorizonDays = 30;

    /// <summary>Ключ горизонта в таблице настроек.</summary>
    public const string NotificationHorizonKey = "notifications.horizon-days";
}

/// <summary>
/// Системные настройки модуля документооборота (§9): чтение с кешем и правка.
/// </summary>
/// <remarks>
/// Кеш обязателен: горизонт читается на КАЖДОМ тике фоновой проверки сроков и при каждой сборке
/// уведомлений, а меняется раз в полгода. Правка кеш сбрасывает — параметр начинает действовать
/// без перезапуска, ради чего настройки и переехали из файла конфигурации в базу.
/// </remarks>
public interface ISystemSettingsStore
{
    /// <summary>Текущие настройки (из кеша, при промахе — из БД).</summary>
    Task<DocFlowSettings> GetAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Сохраняет горизонт уведомлений и сбрасывает кеш. Значение вне границ отклоняется
    /// (<see langword="false"/>) — проверка ЗДЕСЬ, а не только в форме: настройку меняет и командная
    /// строка, и будущий импорт конфигурации.
    /// </summary>
    Task<bool> SetNotificationHorizonAsync(
        int days, int? changedByUserId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Кто вправе вести настройки и справочники модуля.
/// </summary>
/// <remarks>
/// ИНВЕРСИЯ ЗАВИСИМОСТИ, как у <see cref="IDivisionDirectory"/>: право определяется РОЛЬЮ, роли ведёт
/// профиль, а модуль на профиль не ссылается (ADR-0017). Поэтому модуль объявляет порт, а реализацию
/// даёт профиль. Fail-closed: если профиль реализацию не дал, разрешения нет ни у кого — настройка
/// останется на значении по умолчанию, но чужой рукой её не поменяют.
/// </remarks>
public interface IDocFlowAdministration
{
    /// <summary>Вправе ли ТЕКУЩИЙ субъект менять настройки и справочники модуля.</summary>
    Task<bool> CanManageAsync(CancellationToken cancellationToken = default);
}
