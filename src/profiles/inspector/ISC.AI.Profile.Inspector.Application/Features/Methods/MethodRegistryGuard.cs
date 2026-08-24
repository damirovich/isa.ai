using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Inspector.Domain.Enums;
using ISC.AI.Profile.Inspector.Domain.Services;

namespace ISC.AI.Profile.Inspector.Application.Features.Methods;

/// <summary>
/// Кто что может в реестре методик (§5.2.9): СОХРАНЯЮТ те, кто ведёт проверочную работу
/// (Инспектор/Руководитель/Администратор — как в учёте нарушений); УТВЕРЖДАЮТ, правят и удаляют —
/// Руководитель и Администратор (утверждение делает методику эталоном для всех — ответственность
/// выше). Пока ролей нет ни у кого — любой вошедший («замок без ключа», §6.4.1).
/// </summary>
public static class MethodRegistryGuard
{
    /// <summary>Единый текст отказа на сохранение.</summary>
    public const string SaveDenied = "Сохранять методики могут Инспектор, Руководитель и Администратор.";

    /// <summary>Единый текст отказа на утверждение/правку/удаление.</summary>
    public const string ManageDenied = "Утверждают, правят и удаляют методики Руководитель и Администратор.";

    /// <summary>Может ли вызывающий сохранить методику в реестр.</summary>
    public static Task<bool> CallerCanSaveAsync(
        IUserRoleStore roles, ISubjectProvider subjectProvider, CancellationToken cancellationToken) =>
        CheckAsync(roles, subjectProvider,
            static role => role is UserRole.Inspector or UserRole.Manager or UserRole.Administrator,
            cancellationToken);

    /// <summary>Может ли вызывающий утверждать, править и удалять методики.</summary>
    public static Task<bool> CallerCanManageAsync(
        IUserRoleStore roles, ISubjectProvider subjectProvider, CancellationToken cancellationToken) =>
        CheckAsync(roles, subjectProvider,
            static role => role is UserRole.Manager or UserRole.Administrator,
            cancellationToken);

    private static async Task<bool> CheckAsync(
        IUserRoleStore roles, ISubjectProvider subjectProvider,
        Func<UserRole?, bool> allowed, CancellationToken cancellationToken)
    {
        // Fail-closed: неизвестный субъект не пишет ничего.
        if (await subjectProvider.GetCurrentUserIdAsync(cancellationToken) is not { } callerId)
        {
            return false;
        }

        if (allowed(await roles.GetRoleAsync(callerId, cancellationToken)))
        {
            return true;
        }

        // Первичная настройка: ролей ещё нет ни у кого — иначе систему невозможно наполнить.
        return !await roles.AnyAdministratorAsync(cancellationToken);
    }
}
