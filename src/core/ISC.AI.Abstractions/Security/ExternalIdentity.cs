namespace ISC.AI.Abstractions.Security;

/// <summary>
/// Подтверждённая учётка из внешней системы идентификации (Э3-08, ТБ-010/013). Ядро доменно-нейтрально:
/// какая именно система стоит за портом (ведомственная БД пользователей, SSO и т.п.) — решает хост
/// на точке композиции. Допуск (гриф/подразделения) сюда НЕ входит — он ведётся локально в ядре
/// (<c>core.clearance</c>) и по принципу default-deny не выводится из внешней учётки (ТБ-012/021).
/// </summary>
/// <param name="ExternalId">Стабильный идентификатор учётки во внешней системе (строкой).</param>
/// <param name="Login">Имя входа.</param>
/// <param name="DisplayName">Отображаемое имя (ФИО), если внешняя система его ведёт.</param>
/// <param name="DepartmentCode">Код подразделения во внешней системе (для сопоставления по значению, ТО-инф-06).</param>
/// <param name="SecurityStamp">
/// Штамп безопасности внешней системы: меняется при смене пароля/блокировке. Сессия, чей штамп
/// разошёлся с текущим, подлежит завершению (ТБ-014/016).
/// </param>
public sealed record ExternalIdentity(
    string ExternalId,
    string Login,
    string? DisplayName,
    string? DepartmentCode,
    string SecurityStamp);
