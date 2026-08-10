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
    /// Правило ОДНО с ведением учётных записей и журналом аудита (<see cref="AdministrationRule"/>):
    /// Администратор — всегда; любой вошедший — только пока Администратора нет. Иначе после развёртывания
    /// настройки оказались бы недоступны вообще никому (замок без ключа, 6.4.1).
    /// </remarks>
    public Task<bool> CanManageAsync(CancellationToken cancellationToken = default) =>
        AdministrationRule.CallerCanManageAsync(roles, subjectProvider, cancellationToken);
}
