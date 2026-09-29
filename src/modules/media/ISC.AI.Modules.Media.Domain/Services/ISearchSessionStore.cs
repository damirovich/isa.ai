using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.Media.Domain.Model;

namespace ISC.AI.Modules.Media.Domain.Services;

/// <summary>
/// Хранилище поисковых сессий, кандидат-листов и решений верификации (ТО-инф-12, ТФ-ПЛ-07, ТФ-ВЕР-01/02).
/// Схема <c>media</c>; дело и основание — непрозрачные значения профиля. Реализация — <c>Media.Data</c>.
/// </summary>
/// <remarks>
/// Чтение — всегда под floor ядра (ТБ-020): сессия и кандидаты несут гриф/подразделение дела. Запись
/// решения и нового статуса — ОДНОЙ транзакцией: статус считает вызывающий по <see cref="TwoPersonRule"/>,
/// хранилище его лишь фиксирует; иных путей изменить статус нет (ТБ-073).
/// </remarks>
public interface ISearchSessionStore
{
    /// <summary>Создать сессию с кандидат-листом (в порядке ранга); возвращает идентификатор сессии.</summary>
    Task<int> CreateAsync(SearchSessionDraft draft, IReadOnlyList<FaceCandidate> candidates, CancellationToken cancellationToken = default);

    /// <summary>Сессия, если доступна субъекту.</summary>
    Task<SearchSessionRow?> GetAsync(int sessionId, AccessContext access, CancellationToken cancellationToken = default);

    /// <summary>Сессии дела (история поисков, ТФ-ПЛ-07), новые первыми.</summary>
    Task<IReadOnlyList<SearchSessionRow>> ListByCaseAsync(int caseId, AccessContext access, CancellationToken cancellationToken = default);

    /// <summary>Кандидаты сессии по рангу (с решениями — для эксперта после решения и руководителя).</summary>
    Task<IReadOnlyList<SearchCandidateRow>> ListCandidatesAsync(int sessionId, AccessContext access, CancellationToken cancellationToken = default);

    /// <summary>Кандидат, если доступен.</summary>
    Task<SearchCandidateRow?> GetCandidateAsync(int candidateId, AccessContext access, CancellationToken cancellationToken = default);

    /// <summary>
    /// Очередь стадии: <see cref="VerificationStage.Expert"/> — статус <see cref="CandidateStatus.Candidate"/>,
    /// <see cref="VerificationStage.Verifier"/> — <see cref="CandidateStatus.PendingVerifier"/>; только дела
    /// из <paramref name="caseIds"/> и только в пределах допуска.
    /// </summary>
    Task<IReadOnlyList<SearchCandidateRow>> ListQueueAsync(VerificationStage stage, IReadOnlyCollection<int> caseIds, AccessContext access, CancellationToken cancellationToken = default);

    /// <summary>
    /// Одна страница очереди стадии (те же правила отбора, что у <see cref="ListQueueAsync"/>) и общее число
    /// кандидатов в очереди в пределах допуска. Порядок устойчивый — сессия, ранг, идентификатор, — чтобы страницы
    /// не перекрывались и не теряли строк между запросами.
    /// </summary>
    /// <param name="stage">Стадия верификации.</param>
    /// <param name="caseIds">Область дел субъекта (ТБ-071); пустая — пустая очередь.</param>
    /// <param name="skip">Сколько строк пропустить (≥ 0).</param>
    /// <param name="take">Сколько строк взять (≥ 1).</param>
    /// <param name="access">Контекст допуска.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    Task<VerificationQueuePage> ListQueuePageAsync(
        VerificationStage stage, IReadOnlyCollection<int> caseIds, int skip, int take, AccessContext access, CancellationToken cancellationToken = default);

    /// <summary>Записать решение и новый статус атомарно; привязка к фигуранту (<paramref name="personRef"/>) — если указана.</summary>
    Task RecordDecisionAsync(int candidateId, VerificationDecision decision, CandidateStatus newStatus, int? personRef, CancellationToken cancellationToken = default);
}

/// <summary>Страница очереди верификации: строки страницы и общее число кандидатов в очереди.</summary>
/// <param name="Rows">Кандидаты страницы.</param>
/// <param name="Total">Всего кандидатов в очереди стадии в пределах допуска и области дел.</param>
public sealed record VerificationQueuePage(IReadOnlyList<SearchCandidateRow> Rows, int Total);
