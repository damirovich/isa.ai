using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Inspector.Domain.Services;

namespace ISC.AI.Profile.Inspector.Application.Features.Norms;

/// <summary>
/// Кто вправе вести картотеку НПА. Ведение — административное действие: смена статуса редакции
/// меняет видимость материала в поиске и грунтовке ДЛЯ ВСЕХ пользователей (GATE-3), поэтому правило
/// то же, что у остальных справочников профиля (<see cref="AdministrationRule"/>): Администратор —
/// всегда; любой вошедший — только пока Администратора нет («замок без ключа», 6.4.1).
/// </summary>
/// <remarks>
/// Осознанное расхождение с «Загрузкой корпуса» (она ролью не ограничена): загрузка ДОБАВЛЯЕТ
/// материал под декларированным грифом, а картотека может СКРЫТЬ действующий — цена ошибки выше.
/// Если заказчик решит отдать ведение шире (например, Инспектору) — правка в одном месте.
/// </remarks>
public static class NormGuard
{
    /// <summary>Единый текст отказа.</summary>
    public const string Denied = "Ведение картотеки НПА доступно только Администратору.";

    /// <inheritdoc cref="NormGuard" />
    public static Task<bool> CallerCanManageAsync(
        IUserRoleStore roles, ISubjectProvider subjectProvider, CancellationToken cancellationToken) =>
        AdministrationRule.CallerCanManageAsync(roles, subjectProvider, cancellationToken);
}
