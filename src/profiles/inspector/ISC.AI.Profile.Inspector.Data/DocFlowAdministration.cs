using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.DocFlow.Domain.Services;
using ISC.AI.Profile.Inspector.Domain.Services;

namespace ISC.AI.Profile.Inspector.Data;

/// <summary>
/// Реализация порта <see cref="IDocFlowAdministration"/> — кто вправе вести настройки модуля
/// документооборота. Инверсия зависимости: право определяется РОЛЬЮ, роли ведёт профиль, а модуль
/// на профиль не ссылается (ADR-0017) — тот же приём, что у справочника подразделений.
/// </summary>
public sealed class DocFlowAdministration(IUserRoleStore roles, ISubjectProvider subjectProvider)
    : IDocFlowAdministration
{
    /// <inheritdoc />
    /// <remarks>
    /// Право «Типы документов и настройки» матрицы доступа (ADR-0033): по умолчанию Администратор; любой
    /// вошедший — только пока Администратора нет. Иначе после развёртывания
    /// настройки оказались бы недоступны вообще никому (замок без ключа, 6.4.1).
    /// </remarks>
    public Task<bool> CanManageAsync(CancellationToken cancellationToken = default) =>
        PermissionRule.CallerHasAsync(roles, subjectProvider, InspectorPermissions.DocFlowSettings, cancellationToken);

    /// <inheritdoc />
    /// <remarks>
    /// Право «Снятие поручения с контроля» матрицы доступа (ADR-0033): по умолчанию Руководитель (ТЗ СКИД §4.2). Режима
    /// первичной настройки у права нет: снятие — не настройка, а без Руководителя его выдаёт Администратор в матрице.
    /// </remarks>
    public Task<bool> CanCloseAssignmentsAsync(CancellationToken cancellationToken = default) =>
        PermissionRule.CallerHasAsync(roles, subjectProvider, InspectorPermissions.DocFlowClose, cancellationToken);
}
