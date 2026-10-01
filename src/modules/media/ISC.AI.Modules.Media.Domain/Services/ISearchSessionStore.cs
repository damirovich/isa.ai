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
        VerificationStage stage, IReadOnlyCollection<int> caseIds, int skip, int take, AccessContext access,
        VerificationQueueFilter? filter = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Предлагала ли уже система (ТФ-ПЕР-09) в этом деле этого фигуранта по этому эталону на этом носителе — в любом
    /// статусе. Повторная индексация носителя пересоздаёт лица с новыми идентификаторами, поэтому сверка идёт по
    /// носителю, а не по лицу: разобранное или ждущее разбора предложение второй раз не ставится.
    /// </summary>
    /// <remarks>Без решётки: вызывает фоновый конвейер, ответ наружу не выдаётся.</remarks>
    Task<bool> HasSuggestionAsync(int caseId, int personRef, int probeFaceId, int assetId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Кандидаты «предложено системой» (ТФ-ПЕР-09) на лицах носителя в делах <paramref name="caseIds"/> — для
    /// карточки носителя; под решёткой (ТБ-020/021), дела вне списка не читаются (ТБ-071). Предложенный фигурант
    /// отдаётся только у кандидатов, ждущих эксперта (слепая проекция верификатора, ТФ-ВЕР-02).
    /// </summary>
    Task<IReadOnlyList<SuggestedCandidateRow>> ListSuggestedForAssetAsync(
        int assetId, IReadOnlyCollection<int> caseIds, AccessContext access, CancellationToken cancellationToken = default);

    /// <summary>Записать решение и новый статус атомарно; привязка к фигуранту (<paramref name="personRef"/>) — если указана.</summary>
    Task RecordDecisionAsync(int candidateId, VerificationDecision decision, CandidateStatus newStatus, int? personRef, CancellationToken cancellationToken = default);
}

/// <summary>Страница очереди верификации: строки страницы и общее число кандидатов в очереди.</summary>
/// <param name="Rows">Кандидаты страницы.</param>
/// <param name="Total">Всего кандидатов в очереди стадии в пределах допуска и области дел.</param>
public sealed record VerificationQueuePage(IReadOnlyList<SearchCandidateRow> Rows, int Total);

/// <summary>Порядок очереди верификации.</summary>
public enum VerificationQueueOrder
{
    /// <summary>По поисковым сессиям и рангу — кандидаты одного поиска рядом (как раньше).</summary>
    BySession = 0,

    /// <summary>Сначала самые похожие.</summary>
    MostSimilar = 1,

    /// <summary>Сначала из новых поисков.</summary>
    Newest = 2,
}

/// <summary>
/// Отбор очереди верификации (ТФ-ПЛ-02): применяется на стороне БД поверх решётки и области дел — сужает, никогда не
/// расширяет. Слепоту (ТБ-073) не затрагивает: отбирает по изображению и материалу, не по решениям и фигуранту.
/// </summary>
/// <param name="MinSimilarity">Схожесть не ниже (0..1); <see langword="null"/> — любая.</param>
/// <param name="CaseId">Только это дело (из области субъекта); <see langword="null"/> — все.</param>
/// <param name="MaterialFromUtc">Материал снят (или загружен, если время съёмки неизвестно) не раньше.</param>
/// <param name="MaterialToUtc">…и не позже (включительно).</param>
/// <param name="Order">Порядок.</param>
public sealed record VerificationQueueFilter(
    double? MinSimilarity = null,
    int? CaseId = null,
    DateTime? MaterialFromUtc = null,
    DateTime? MaterialToUtc = null,
    VerificationQueueOrder Order = VerificationQueueOrder.BySession);
