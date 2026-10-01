namespace ISC.AI.Modules.DocFlow.Domain.Services;

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

    /// <summary>
    /// Вправе ли ТЕКУЩИЙ субъект снимать назначение с контроля (финальный статус «Снято с контроля», ТЗ СКИД §4.2:
    /// «только Руководитель»). Какая роль профиля считается Руководителем — решает профиль.
    /// </summary>
    Task<bool> CanCloseAssignmentsAsync(CancellationToken cancellationToken = default);
}
