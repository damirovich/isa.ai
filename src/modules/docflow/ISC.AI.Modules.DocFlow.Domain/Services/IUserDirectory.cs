namespace ISC.AI.Modules.DocFlow.Domain.Services;

/// <summary>Строка справочника пользователей для выбора в UI (исполнитель, инспектор).</summary>
public sealed record UserItem(int Id, string Name);

/// <summary>
/// Порт справочника пользователей. Реестр пользователей — <c>core.app_user</c> (решение по вопросу 4
/// Э4-35: реестр переезжает в ISC.AI); реализация — в слое данных модуля поверх контекста ядра
/// (<c>*.Data</c> → <c>Persistence</c> разрешено правилом зависимостей).
/// </summary>
public interface IUserDirectory
{
    /// <summary>Список активных пользователей (идентификатор + отображаемое имя), по алфавиту.</summary>
    Task<IReadOnlyList<UserItem>> ListActiveAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Отображаемое имя одного пользователя; <see langword="null"/> — такого пользователя нет.
    /// Нужно для подстановки автора в текст уведомления (разд. 5): тянуть ради одного имени весь
    /// список активных было бы расточительно, а неактивный автор из списка вообще выпадает.
    /// </summary>
    Task<string?> GetNameAsync(int userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Разрешено ли пользователю подразделение <paramref name="divisionId"/> — проверка перед тем, как
    /// назначить его исполнителем (§4.1/§4.7).
    /// </summary>
    /// <remarks>
    /// ЗАМЕНА ПРАВИЛА СКИД, а не его копия. Там исполнитель обязан был состоять В ТОМ ЖЕ подразделении,
    /// что и назначение (<c>user.DepartmentId == assignment.DepartmentId</c>). У нас поля
    /// «подразделение пользователя» НЕТ вовсе: реестр — <c>core.app_user</c> (вопрос 4), и связь
    /// человека с подразделениями выражена ДОПУСКОМ (<c>core.clearance.division_scope</c>). Проверяем
    /// по нему: смысл тот же и даже строже по последствиям — иначе исполнителю поручат документ,
    /// которого он не увидит (решётка ТБ-020/021 отфильтрует его на выборке).
    /// Неактивный пользователь и пользователь без допуска дают <see langword="false"/> (fail-closed).
    /// </remarks>
    Task<bool> CanSeeDivisionAsync(int userId, int divisionId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Кого предлагать в полях «ответственный инспектор» и «исполнитель» (§3.2/§4.1).
/// </summary>
/// <remarks>
/// ЗАЧЕМ ОТДЕЛЬНЫЙ ПОРТ, а не фильтр в <see cref="IUserDirectory"/>. Ответ зависит от РОЛИ, а роли
/// ведёт профиль; модуль слова «роль» не знает и знать не должен (ADR-0017). Поэтому модуль
/// спрашивает ПО СМЫСЛУ — «кто может быть инспектором», «кто может исполнять в подразделении N», —
/// а профиль отвечает, сверяясь со своими ролями и допусками. Та же инверсия, что у справочника
/// подразделений.
///
/// Это УДОБСТВО ФОРМЫ, а не защита: сервер всё равно проверяет исполнителя отдельно
/// (<see cref="IUserDirectory.CanSeeDivisionAsync"/>) — форму можно обойти запросом.
/// </remarks>
public interface IAssignmentCandidateDirectory
{
    /// <summary>Кандидаты в ответственные инспекторы документа.</summary>
    Task<IReadOnlyList<UserItem>> ListInspectorsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Кандидаты в исполнители по подразделению: те, кому это подразделение доступно.
    /// </summary>
    /// <remarks>
    /// Отбор ПО ПОДРАЗДЕЛЕНИЮ обязателен: исполнитель, которому подразделение не разрешено, не увидит
    /// документ, который ему поручают (решётка ТБ-020/021 отфильтрует документ на выборке), и ошибка
    /// вылезет уже после регистрации — поручение будет числиться, а человек о нём не узнает.
    /// </remarks>
    Task<IReadOnlyList<UserItem>> ListAssigneesAsync(
        int divisionId, CancellationToken cancellationToken = default);
}
