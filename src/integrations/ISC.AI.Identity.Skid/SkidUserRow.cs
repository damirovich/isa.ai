namespace ISC.AI.Identity.Skid;

/// <summary>
/// Строка таблицы <c>public.users</c> БД СКИД (read-only проекция чужой схемы; ISC.AI её НЕ мигрирует
/// и НЕ пишет). Имена колонок — snake_case соглашением контекста.
/// </summary>
public class SkidUserRow
{
    /// <summary>Идентификатор пользователя СКИД.</summary>
    public Guid Id { get; set; }

    /// <summary>Имя входа (уникально в СКИД).</summary>
    public required string Login { get; set; }

    /// <summary>Хеш пароля — Argon2id, PHC-строка libsodium (<c>$argon2id$v=19$…</c>).</summary>
    public required string PasswordHash { get; set; }

    /// <summary>ФИО.</summary>
    public required string FullName { get; set; }

    /// <summary>Заблокирован ли пользователь в СКИД.</summary>
    public bool IsBlocked { get; set; }

    /// <summary>Штамп безопасности: меняется при смене пароля/блокировке — инвалидация сессий.</summary>
    public required string SecurityStamp { get; set; }

    /// <summary>
    /// Пользователю выдан временный пароль (создание учётки/сброс администратором) и в самой СКИД
    /// вход принудит его сменить. Известен третьему лицу (администратору, выдавшему сброс) —
    /// до смены не считается полноценной учёткой для входа в ISC.AI.
    /// </summary>
    public bool MustChangePassword { get; set; }

    /// <summary>Подразделение пользователя в СКИД (плоский справочник), если задано.</summary>
    public Guid? DepartmentId { get; set; }
}
