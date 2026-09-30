using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Inspector.Domain.Services;

namespace ISC.AI.Profile.Inspector.Application.Features.Violations;

/// <summary>
/// Кто вправе заносить и править нарушения — право «Учёт нарушений: запись и правка» матрицы доступа (ADR-0033). По
/// умолчанию Инспектор, Руководитель, Администратор — те, кто ведёт проверочную работу; Исполнителю запись не положена
/// (он объект контроля, §2.1). Пока Администратора в системе нет — любой вошедший («замок без ключа», 6.4.1).
/// </summary>
public static class ViolationGuard
{
    /// <summary>Единый текст отказа.</summary>
    public static readonly string Denied = PermissionRule.Denied(InspectorPermissions.ViolationsEdit);

    /// <inheritdoc cref="ViolationGuard" />
    public static Task<bool> CallerCanManageAsync(
        IUserRoleStore roles, ISubjectProvider subjectProvider, CancellationToken cancellationToken) =>
        PermissionRule.CallerHasAsync(roles, subjectProvider, InspectorPermissions.ViolationsEdit, cancellationToken);
}
