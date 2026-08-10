using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Inspector.Domain.Enums;
using ISC.AI.Profile.Inspector.Domain.Services;

namespace ISC.AI.Profile.Inspector.Application.Features.Roles;

/// <summary>Общая проверка вызывающего для обоих сценариев (см. remarks класса).</summary>
/// <remarks>
/// Опирается на <see cref="ISubjectProvider"/> («кто вошёл»), а НЕ на <c>IAccessContextProvider</c>
/// («что вошедшему можно»). Это исправление отдельного отказа, зафиксированного проверкой 6.4.2:
/// на ЧИСТОЙ установке ни у кого нет записи в <c>core.clearance</c>, поэтому <c>GetCurrentAsync</c>
/// бросал <c>AccessContextRequiredException</c> — и страница ролей падала ИМЕННО ТАМ, где режим
/// первичной настройки и нужен. Право распоряжаться ролями определяется ролью, а не допуском.
/// </remarks>
internal static class RoleScenariosGuard
{
    /// <summary>
    /// Вправе ли вызывающий видеть и назначать роли. Обычное правило — только Администратор; но пока
    /// В СИСТЕМЕ НЕТ НИ ОДНОГО АДМИНИСТРАТОРА, действует РЕЖИМ ПЕРВИЧНОЙ НАСТРОЙКИ: иначе назначить
    /// первого Администратора некому (страница требует Администратора — замок без ключа; ровно так
    /// система и оказалась запертой сразу после выпуска этапа 6.4).
    /// </summary>
    /// <remarks>
    /// Условие — «нет Администратора», НЕ «реестр ролей пуст». Проверка пустоты (первая редакция
    /// фикса) закрывала окно ЛЮБОЙ первой ролью: назначил себе «Руководителя» (единственная роль,
    /// которая видит все документы) — Администратора нет, окно закрыто, управление ролями потеряно
    /// навсегда. Текущее правило самовосстанавливающееся: не стало Администратора — окно открылось.
    /// </remarks>
    public static async Task<bool> CallerCanManageRolesAsync(
        IUserRoleStore store, ISubjectProvider subjectProvider, CancellationToken cancellationToken)
    {
        if (await subjectProvider.GetCurrentUserIdAsync(cancellationToken) is not { } callerId)
        {
            return false;
        }

        if (await store.GetRoleAsync(callerId, cancellationToken) == UserRole.Administrator)
        {
            return true;
        }

        return !await store.AnyAdministratorAsync(cancellationToken);
    }
}
