using ISC.AI.Abstractions.Security;

namespace ISC.AI.Modules.Media.Domain.Services;

/// <summary>
/// Счётчики результативности распознавания по одному подразделению за период (ТФ-ОТЧ-07, ADR-0061). Носители и лица —
/// по подразделению носителя, поиски и их исходы — по подразделению сессии поиска.
/// </summary>
/// <param name="DivisionId">Подразделение.</param>
/// <param name="ImagesProcessed">Проиндексировано фото.</param>
/// <param name="VideosProcessed">Проиндексировано видео.</param>
/// <param name="FacesFound">Найдено лиц на этих носителях.</param>
/// <param name="FacesRejected">Из них непригодных для сравнения по качеству (ТО-мат-07).</param>
/// <param name="Searches">Сессий поиска по лицу, начатых в периоде (оператором и автосверкой).</param>
/// <param name="AutoSearches">Из них автосверок (ТФ-ПЕР-09).</param>
/// <param name="Matches">Кандидатов выдачи этих сессий — совпадений, прошедших порог сессии.</param>
/// <param name="Confirmed">Кандидатов, подтверждённых верификацией.</param>
/// <param name="Rejected">Кандидатов, отклонённых верификацией.</param>
/// <param name="Undetermined">Кандидатов с итогом «не определено».</param>
/// <param name="Pending">Кандидатов, ещё ожидающих решения.</param>
public sealed record RecognitionCounters(
    int DivisionId,
    int ImagesProcessed,
    int VideosProcessed,
    int FacesFound,
    int FacesRejected,
    int Searches,
    int AutoSearches,
    int Matches,
    int Confirmed,
    int Rejected,
    int Undetermined,
    int Pending)
{
    /// <summary>Пустые счётчики подразделения.</summary>
    public static RecognitionCounters Empty(int divisionId) => new(divisionId, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

    /// <summary>Сумма счётчиков (для итога); подразделение — у левого слагаемого.</summary>
    public RecognitionCounters Plus(RecognitionCounters other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return new RecognitionCounters(
            DivisionId,
            ImagesProcessed + other.ImagesProcessed,
            VideosProcessed + other.VideosProcessed,
            FacesFound + other.FacesFound,
            FacesRejected + other.FacesRejected,
            Searches + other.Searches,
            AutoSearches + other.AutoSearches,
            Matches + other.Matches,
            Confirmed + other.Confirmed,
            Rejected + other.Rejected,
            Undetermined + other.Undetermined,
            Pending + other.Pending);
    }
}

/// <summary>Счётчики распознавания по подразделениям и наибольший гриф записей, вошедших в счёт.</summary>
/// <param name="Divisions">Подразделения, у которых в периоде есть хоть одна запись; по возрастанию номера.</param>
/// <param name="MaxClassification">Наибольший гриф вошедших записей — гриф отчёта (ТБ-033); пусто — 0.</param>
public sealed record RecognitionStatistics(IReadOnlyList<RecognitionCounters> Divisions, short MaxClassification);

/// <summary>
/// Статистика распознавания лиц для отчётности (ТФ-ОТЧ-07, ADR-0061): детерминированный счёт записей схемы
/// <c>media</c> за период, без ИИ. Реализует пакет (слой данных).
/// </summary>
/// <remarks>
/// ИНВАРИАНТ (ТБ-020/021): счёт идёт на стороне БД под той же решёткой, что каталог пакета, — каждая запись
/// (носитель, лицо, сессия, кандидат) входит, только если видна субъекту <see cref="AccessContext"/>; кандидат — ещё
/// и если видна его сессия. Невидимое не попадает ни в счётчики, ни в гриф.
/// </remarks>
public interface IMediaStatistics
{
    /// <summary>
    /// Счётчики распознавания за период [<paramref name="fromUtc"/>; <paramref name="toUtc"/>) по подразделениям.
    /// </summary>
    /// <param name="fromUtc">Начало периода, UTC, включительно.</param>
    /// <param name="toUtc">Конец периода, UTC, не включительно.</param>
    /// <param name="divisionId">Одно подразделение; <see langword="null"/> — все видимые.</param>
    /// <param name="access">Контекст доступа субъекта (обязателен).</param>
    /// <param name="cancellationToken">Отмена.</param>
    Task<RecognitionStatistics> RecognitionAsync(
        DateTime fromUtc, DateTime toUtc, int? divisionId, AccessContext access, CancellationToken cancellationToken = default);
}
