using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Inspector.Domain.Services;

namespace ISC.AI.Profile.Inspector.Application.Features.Methods;

/// <summary>
/// Кто что может в реестре методик (§5.2.9) — права матрицы доступа (ADR-0033). По умолчанию СОХРАНЯЮТ те, кто
/// ведёт проверочную работу (Инспектор/Руководитель/Администратор — как в учёте нарушений); УТВЕРЖДАЮТ, правят и
/// удаляют — Руководитель и Администратор (утверждение делает методику эталоном для всех — ответственность выше).
/// Пока Администратора нет ни у кого — любой вошедший («замок без ключа», §6.4.1).
/// </summary>
public static class MethodRegistryGuard
{
    /// <summary>Единый текст отказа на сохранение.</summary>
    public static readonly string SaveDenied = PermissionRule.Denied(InspectorPermissions.MethodsSave);

    /// <summary>Единый текст отказа на утверждение/правку/удаление.</summary>
    public static readonly string ManageDenied = PermissionRule.Denied(InspectorPermissions.MethodsManage);

    /// <summary>Может ли вызывающий сохранить методику в реестр.</summary>
    public static Task<bool> CallerCanSaveAsync(
        IUserRoleStore roles, ISubjectProvider subjectProvider, CancellationToken cancellationToken) =>
        PermissionRule.CallerHasAsync(roles, subjectProvider, InspectorPermissions.MethodsSave, cancellationToken);

    /// <summary>Может ли вызывающий утверждать, править и удалять методики.</summary>
    public static Task<bool> CallerCanManageAsync(
        IUserRoleStore roles, ISubjectProvider subjectProvider, CancellationToken cancellationToken) =>
        PermissionRule.CallerHasAsync(roles, subjectProvider, InspectorPermissions.MethodsManage, cancellationToken);
}
