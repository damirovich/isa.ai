namespace ISC.AI.Web.Security;

/// <summary>Имена клеймов сессии ISC.AI (Э3-08). Cookie несёт ТОЛЬКО идентификацию субъекта —
/// допуск в клеймы не кладётся и читается из БД на каждую операцию (ТБ-016).</summary>
public static class AuthClaims
{
    /// <summary>Идентификатор учётки во внешней системе идентификации.</summary>
    public const string ExternalId = "iscai:ext";

    /// <summary>Штамп безопасности внешней учётки на момент входа (ревалидация сессии, ТБ-014/016).</summary>
    public const string SecurityStamp = "iscai:stamp";

    /// <summary>Отображаемое имя (ФИО) для шапки интерфейса.</summary>
    public const string DisplayName = "iscai:display";

    /// <summary>
    /// Пароль временный — до его смены оболочка не пускает никуда, кроме страницы смены пароля
    /// (Э4-35 §6.5). Клейм ставится при входе по флагу учётки; после смены штамп безопасности
    /// меняется, сессия завершается, и следующий вход происходит уже без него.
    /// </summary>
    public const string MustChangePassword = "iscai:pwdchange";

    /// <summary>
    /// Unix-время (секунды) последней проверки принципала во внешней системе — используется
    /// <c>CookiePrincipalValidator</c>, чтобы не сверять внешнюю систему на КАЖДЫЙ HTTP-запрос
    /// (ТБ-014/016), а раз в окно <c>Auth:RevalidationMinutes</c>.
    /// </summary>
    public const string ValidatedAt = "iscai:validated";
}
