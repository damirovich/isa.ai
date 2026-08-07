namespace ISC.AI.Abstractions.Security;

/// <summary>Строка списка учётных записей (экран администрирования).</summary>
/// <param name="HasLocalPassword">
/// Заведён локальный пароль. <see langword="false"/> — войти по этой учётке нельзя: она либо ещё не
/// перенесена из внешней системы, либо создана без пароля.
/// </param>
/// <param name="MustChangePassword">Пароль временный: выдан администратором и ему известен.</param>
public sealed record UserAccountRow(
    int UserId,
    string UserName,
    string? DisplayName,
    bool IsActive,
    bool HasLocalPassword,
    bool MustChangePassword,
    string? Position = null,
    string? DeactivationReason = null,
    short? MaxClassification = null,
    IReadOnlyList<int>? Divisions = null);

/// <summary>
/// Отбор списка учётных записей.
/// </summary>
/// <param name="Text">Поиск по имени входа, ФИО и должности (без учёта регистра).</param>
/// <param name="IsActive">Только включённые/только отключённые; <see langword="null"/> — все.</param>
/// <param name="DivisionId">Подразделение из допуска пользователя.</param>
/// <param name="RestrictToUserIds">
/// Ограничение множеством идентификаторов; <see langword="null"/> — без ограничения.
/// </param>
/// <param name="Page">Номер страницы, с 1.</param>
/// <param name="PageSize">Размер страницы.</param>
/// <remarks>
/// <paramref name="RestrictToUserIds"/> существует ради фильтра ПО РОЛИ. Роли ведёт профиль в своей
/// схеме, ядро о них не знает и знать не должно, а соединить две схемы одним запросом через два
/// разных контекста нельзя. Поэтому профиль сам превращает «роль» в набор идентификаторов и передаёт
/// его сюда — постраничность при этом остаётся серверной.
/// </remarks>
public sealed record UserAccountFilter(
    string? Text = null,
    bool? IsActive = null,
    int? DivisionId = null,
    IReadOnlyList<int>? RestrictToUserIds = null,
    int Page = 1,
    int PageSize = 25);

/// <summary>Страница списка учётных записей: строки и общее число подходящих.</summary>
public sealed record UserAccountPage(IReadOnlyList<UserAccountRow> Rows, int TotalCount);

/// <summary>Итог смены собственного пароля.</summary>
public enum PasswordChangeStatus
{
    /// <summary>Пароль изменён.</summary>
    Ok,

    /// <summary>Учётной записи нет либо она отключена.</summary>
    NotFound,

    /// <summary>Текущий пароль указан неверно.</summary>
    WrongCurrentPassword,

    /// <summary>У учётной записи нет локального пароля — менять нечего (вход ещё внешний).</summary>
    NoLocalPassword,

    /// <summary>Новый пароль совпадает с текущим — смена бессмысленна.</summary>
    SameAsCurrent,
}

/// <summary>
/// Ведение учётных записей (<c>core.app_user</c>, Э4-35 §6.5): создание, сброс пароля, включение и
/// отключение, смена собственного пароля.
/// </summary>
/// <remarks>
/// ИНВАРИАНТЫ (ТБ-043, ТБ-016). (1) Пароль НИКОГДА не хранится и не возвращается в открытом виде:
/// временный пароль порождает вызывающий и показывает администратору ОДИН раз, в базу уходит только
/// хеш. (2) Любая смена пароля и любое отключение учётки МЕНЯЮТ штамп безопасности — живые сессии
/// этого пользователя завершаются ревалидацией; без ротации отобранный доступ продолжал бы работать
/// до истечения cookie. (3) Порт профиле-нейтрален: кто вправе администрировать — решает профиль.
/// </remarks>
public interface IUserAccountStore
{
    /// <summary>Все учётные записи (включая отключённые), по отображаемому имени.</summary>
    Task<IReadOnlyList<UserAccountRow>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>Страница списка учётных записей с отбором.</summary>
    /// <remarks>
    /// Постраничность серверная — по той же причине, что и в реестре документов: на сотне сотрудников
    /// разницы нет, а на тысяче полный список тянется в память при каждом открытии экрана.
    /// </remarks>
    Task<UserAccountPage> SearchAsync(
        UserAccountFilter filter, CancellationToken cancellationToken = default);

    /// <summary>
    /// Правит справочные поля учётной записи: отображаемое имя и должность.
    /// </summary>
    /// <remarks>
    /// Здесь НЕТ ни роли, ни допуска, и это осознанно. Роль и допуск — разные полномочия с разными
    /// последствиями (роль решает, что человек делает; допуск — что он видит), у каждого свой экран
    /// и своя запись в журнале. Общая форма «поменять всё сразу», как в СКИД, склеивает их в одно
    /// действие: администратор, поправивший опечатку в фамилии, незаметно для себя переутверждает
    /// и права. Здесь меняется только подпись человека.
    /// </remarks>
    Task<bool> UpdateProfileAsync(
        int userId, string? displayName, string? position, CancellationToken cancellationToken = default);

    /// <summary>
    /// Создаёт учётную запись с ВРЕМЕННЫМ паролем (его показывает администратору вызывающий).
    /// Возвращает идентификатор либо <see langword="null"/>, если имя входа уже занято.
    /// </summary>
    Task<int?> CreateAsync(
        string userName, string? displayName, string temporaryPassword,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Сбрасывает пароль на временный: помечает учётку как требующую смены и меняет штамп —
    /// живые сессии владельца обрываются немедленно (ТБ-016).
    /// </summary>
    Task<bool> ResetPasswordAsync(
        int userId, string temporaryPassword, CancellationToken cancellationToken = default);

    /// <summary>
    /// Включает или отключает учётную запись. При отключении штамп меняется — сессии обрываются;
    /// без этого отключённый пользователь работал бы до истечения cookie.
    /// </summary>
    /// <param name="reason">
    /// Причина отключения (при включении игнорируется). Сохраняется в учётке, чтобы через месяц
    /// администратор видел «уволен» прямо в списке, а не поднимал журнал.
    /// </param>
    Task<bool> SetActiveAsync(
        int userId, bool isActive, string? reason = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Смена СОБСТВЕННОГО пароля: обязательна проверка текущего (владение сессией не заменяет знание
    /// пароля — иначе перехваченная сессия позволила бы сменить пароль и закрепиться).
    /// </summary>
    Task<PasswordChangeStatus> ChangeOwnPasswordAsync(
        int userId, string currentPassword, string newPassword, CancellationToken cancellationToken = default);
}
