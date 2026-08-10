using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.DocFlow.Domain.Enums;

namespace ISC.AI.Modules.DocFlow.Domain.Services;

/// <summary>Счётчики поручений для дашборда.</summary>
/// <param name="Active">В работе: зарегистрировано, в работе, на контроле, частично исполнено.</param>
/// <param name="Overdue">Просрочено (ставит только система, §4.2).</param>
/// <param name="DueToday">Срок сегодня.</param>
/// <param name="DueSoon">Срок в пределах горизонта уведомлений, но не сегодня.</param>
/// <param name="Done">Исполнено.</param>
public sealed record AssignmentCounters(int Active, int Overdue, int DueToday, int DueSoon, int Done)
{
    /// <summary>Пустая сводка — законный результат (нечего показывать), а не признак ошибки.</summary>
    public static AssignmentCounters Empty { get; } = new(0, 0, 0, 0, 0);
}

/// <summary>Нагрузка подразделения: сколько поручений в работе и сколько просрочено.</summary>
public sealed record DivisionLoad(int DivisionId, int Active, int Overdue);

/// <summary>Ближайший срок — строка списка «что горит».</summary>
public sealed record UpcomingDeadline(
    int DocumentId,
    int AssignmentId,
    string? RegNumber,
    string ShortContent,
    DateOnly Deadline,
    int DivisionId,
    int? AssigneeUserId,
    AssignmentStatus Status);

/// <summary>
/// Данные дашборда документооборота.
/// </summary>
/// <param name="All">Сводка по всему, что доступно субъекту.</param>
/// <param name="Mine">
/// Личный срез: поручения, где субъект исполнитель ЛИБО инспектор документа. Отдельно от общего,
/// потому что это разные вопросы: «что происходит в подразделении» и «что должен лично я».
/// </param>
/// <param name="ByDivision">Разрез по подразделениям, наиболее загруженные первыми.</param>
/// <param name="Upcoming">Ближайшие сроки, самые срочные первыми.</param>
public sealed record DashboardData(
    AssignmentCounters All,
    AssignmentCounters Mine,
    IReadOnlyList<DivisionLoad> ByDivision,
    IReadOnlyList<UpcomingDeadline> Upcoming);

/// <summary>
/// Источник данных дашборда документооборота (ТЗ СКИД, экран Dashboard).
/// </summary>
/// <remarks>
/// РАЗГРАНИЧЕНИЕ — В ЗАПРОСЕ, тем же предикатом, что список документов и отчёты (ТБ-020/021 +
/// сужающая политика профиля ADR-0014). Дашборд опаснее, чем кажется: счётчик — это агрегат, и
/// «просрочено: 7» при пустом списке документов сообщает о существовании семи недоступных поручений.
/// Поэтому считается не «всё в базе», а ровно то же множество, что субъект видит в реестре.
/// </remarks>
public interface IDashboardDataSource
{
    /// <summary>Сводка на указанный день с горизонтом «скоро» в днях.</summary>
    Task<DashboardData> GetAsync(
        AccessContext access,
        DateOnly today,
        int horizonDays,
        int upcomingCount = 10,
        CancellationToken cancellationToken = default);
}
