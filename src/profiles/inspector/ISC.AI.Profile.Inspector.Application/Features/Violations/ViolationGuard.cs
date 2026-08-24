using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Inspector.Domain.Enums;
using ISC.AI.Profile.Inspector.Domain.Services;

namespace ISC.AI.Profile.Inspector.Application.Features.Violations;

/// <summary>
/// Кто вправе заносить и править нарушения: Инспектор, Руководитель, Администратор — то есть те,
/// кто ведёт проверочную работу; Исполнителю запись не положена (он объект контроля, §2.1).
/// Пока Администратора в системе нет — любой вошедший («замок без ключа», 6.4.1, как у остальных
/// гардов первичной настройки).
/// </summary>
public static class ViolationGuard
{
    /// <summary>Единый текст отказа.</summary>
    public const string Denied = "Учёт нарушений ведут Инспектор, Руководитель и Администратор.";

    /// <inheritdoc cref="ViolationGuard" />
    public static async Task<bool> CallerCanManageAsync(
        IUserRoleStore roles, ISubjectProvider subjectProvider, CancellationToken cancellationToken)
    {
        // Fail-closed: неизвестный субъект не заносит ничего.
        if (await subjectProvider.GetCurrentUserIdAsync(cancellationToken) is not { } callerId)
        {
            return false;
        }

        var role = await roles.GetRoleAsync(callerId, cancellationToken);
        if (role is UserRole.Inspector or UserRole.Manager or UserRole.Administrator)
        {
            return true;
        }

        // Первичная настройка: ролей ещё нет ни у кого — иначе систему невозможно наполнить.
        return !await roles.AnyAdministratorAsync(cancellationToken);
    }
}
