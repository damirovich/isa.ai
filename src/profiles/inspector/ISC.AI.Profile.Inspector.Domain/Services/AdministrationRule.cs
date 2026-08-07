using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Inspector.Domain.Enums;

namespace ISC.AI.Profile.Inspector.Domain.Services;

/// <summary>
/// Кто вправе выполнять административные действия профиля (роли, допуски, учётные записи, настройки
/// подключённых модулей).
/// </summary>
/// <remarks>
/// ЕДИНСТВЕННОЕ место, где записано это правило. Живёт в ДОМЕНЕ, потому что спрашивают его из разных
/// слоёв: прикладные сценарии профиля (учётные записи, журнал аудита) и слой данных, откуда профиль
/// отдаёт модулю документооборота реализацию его порта <c>IDocFlowAdministration</c>. Копия правила
/// в каждом из этих мест рано или поздно разъехалась бы — а разъехавшееся правило доступа замечает
/// не разработчик, а посторонний.
///
/// Само правило: Администратор — всегда; любой вошедший — ТОЛЬКО пока Администратора в системе нет.
/// Вторая половина не послабление, а выход из «замка без ключа» (6.4.1): после чистого развёртывания
/// ролей не назначено никому, и без неё систему невозможно было бы настроить вообще.
/// </remarks>
public static class AdministrationRule
{
    /// <summary>Вправе ли текущий субъект администрировать систему.</summary>
    public static async Task<bool> CallerCanManageAsync(
        IUserRoleStore roles, ISubjectProvider subjectProvider, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(roles);
        ArgumentNullException.ThrowIfNull(subjectProvider);

        // Fail-closed: неизвестный субъект не администрирует ничего.
        if (await subjectProvider.GetCurrentUserIdAsync(cancellationToken) is not { } callerId)
        {
            return false;
        }

        if (await roles.GetRoleAsync(callerId, cancellationToken) == UserRole.Administrator)
        {
            return true;
        }

        return !await roles.AnyAdministratorAsync(cancellationToken);
    }
}
