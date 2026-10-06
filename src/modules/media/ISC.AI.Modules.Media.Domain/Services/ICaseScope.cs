using ISC.AI.Abstractions.Security;

namespace ISC.AI.Modules.Media.Domain.Services;

/// <summary>Дело, как его видит пакет «Медиа»: непрозрачный идентификатор, подпись и режимные поля.</summary>
/// <param name="CaseId">Идентификатор дела в схеме профиля.</param>
/// <param name="Number">Номер дела (для подписи).</param>
/// <param name="Title">Краткое название (для подписи).</param>
/// <param name="Classification">Гриф дела — наследуется носителями и шаблонами (ТБ-070).</param>
/// <param name="DivisionId">Подразделение дела.</param>
/// <param name="IsClosed">
/// Дело закрыто. Модуль по умолчанию НЕ берёт закрытые дела в область поиска: следователь ищет по
/// текущей работе, и попадание в кандидат-лист лиц из давно оконченных дел — шум. Включаются они
/// осознанно, отдельным признаком запроса (ТФ-ПЛ-05), и факт включения идёт в журнал (ТБ-072).
/// </param>
public sealed record CaseScopeItem(
    int CaseId, string Number, string Title, short Classification, int DivisionId, bool IsClosed = false);

/// <summary>Фигурант дела для привязки кандидата (ТФ-ВЕР-03); модуль знает только идентификатор и подпись.</summary>
/// <param name="PersonId">Фигурант.</param>
/// <param name="DisplayName">Подпись.</param>
/// <param name="ConfirmedOnFace">
/// Лицо кандидата у этого фигуранта уже подтверждено появлением: повторное «подтверждён» новой записи не даст
/// (одно появление на пару «фигурант — лицо»). Заполняется только для стадии эксперта.
/// </param>
public sealed record CasePersonItem(int PersonId, string DisplayName, bool ConfirmedOnFace = false);

/// <summary>Основание поиска в деле (ТБ-071): непрозрачный идентификатор профиля и реквизиты для аудита.</summary>
public sealed record CaseAuthorizationItem(int AuthorizationId, string Reference);

/// <summary>
/// Подтверждённое появление фигуранта на носителе (ТФ-ПЕР-02) — для отметок на ленте видео (ADR-0038): кто и на каком
/// лице подтверждён двумя сотрудниками (ТБ-073). Отозванные появления сюда не попадают.
/// </summary>
/// <param name="PersonId">Фигурант.</param>
/// <param name="DisplayName">Подпись фигуранта.</param>
/// <param name="FaceId">Лицо носителя, как его запомнило появление (после переиндексации — прежний номер).</param>
/// <param name="FrameTimestampMs">Момент кадра лица, мс (для поиска трека после переиндексации); у фото — <see langword="null"/>.</param>
public sealed record AssetAppearanceItem(int PersonId, string DisplayName, int FaceId, long? FrameTimestampMs);

/// <summary>Подтверждённый кандидат (два «подтверждён» разных субъектов, ТБ-073) — материал для «появления» фигуранта (ТФ-ПЕР-02).</summary>
public sealed record ConfirmedAppearance(
    int CaseId,
    int PersonRef,
    int SessionId,
    int CandidateId,
    int FaceId,
    int AssetId,
    int? FrameIndex,
    long? FrameTimestampMs,
    double Similarity,
    short Classification,
    int DivisionId,
    int ExpertUserId,
    int VerifierUserId,
    DateTime ConfirmedAtUtc);

/// <summary>
/// Дело, в котором после индексации носителя ищутся фигуранты (ТФ-ПЕР-09): режимные поля дела — потолок, под
/// которым система сравнивает лица (не выше дела), основание поиска (ТБ-071) и действующие эталоны фигурантов.
/// </summary>
/// <param name="CaseId">Дело (непрозрачный идентификатор профиля).</param>
/// <param name="Classification">Гриф дела — потолок допуска системы при сравнении (ТБ-020/070).</param>
/// <param name="DivisionId">Подразделение дела — единственное подразделение в допуске системы.</param>
/// <param name="AuthorizationRef">Реквизиты основания поиска для сессии и аудита (ТБ-071/072).</param>
/// <param name="References">Действующие эталонные лица фигурантов дела.</param>
public sealed record SuggestionTarget(
    int CaseId,
    short Classification,
    int DivisionId,
    string AuthorizationRef,
    IReadOnlyList<SuggestionReference> References);

/// <summary>Эталонное лицо фигуранта для автоматического предложения (ТФ-ПЕР-09).</summary>
/// <param name="PersonId">Фигурант (непрозрачный идентификатор профиля).</param>
/// <param name="FaceId">Лицо эталона в схеме <c>media</c>.</param>
public sealed record SuggestionReference(int PersonId, int FaceId);

/// <summary>
/// Готово ли дело к автоматической сверке (ТФ-ПЕР-09) — для объяснения на карточке носителя. Правило то же, что у
/// <see cref="SuggestionTarget"/>: открытое дело, основание поиска, действующие эталоны с лицом.
/// </summary>
/// <param name="CaseId">Дело.</param>
/// <param name="CaseNumber">Номер дела для подписи.</param>
/// <param name="IsOpen">Дело не закрыто.</param>
/// <param name="HasBasis">Есть действующее основание поиска (или задание по объекту, ТБ-071).</param>
/// <param name="ReferenceCount">Сколько действующих эталонов с лицом у фигурантов дела.</param>
/// <param name="LatestReferenceAtUtc">Когда добавлен самый свежий из них — сверка раньше этого времени устарела.</param>
public sealed record SuggestionReadiness(
    int CaseId,
    string CaseNumber,
    bool IsOpen,
    bool HasBasis,
    int ReferenceCount,
    DateTime? LatestReferenceAtUtc);

/// <summary>
/// ПОРТ ПРОФИЛЯ: область дел субъекта (ТБ-071, ТФ-ПЛ-05) и связь носителей/фигурантов с делами. Модуль
/// понятия «дело» не имеет — он оперирует непрозрачными идентификаторами; всё про доступность дел по
/// роли и владению знает только профиль. Без реализации приложение не стартует (ТС-013).
/// </summary>
/// <remarks>
/// Fail-closed: <see cref="GetCaseAsync"/> возвращает <see langword="null"/> и для несуществующего, и для
/// недоступного дела — снаружи они неотличимы (ТБ-020/021). Все методы с <see cref="AccessContext"/>
/// обязаны применять floor ядра поверх ролевых правил.
/// </remarks>
public interface ICaseScope
{
    /// <summary>Дела, доступные субъекту (для выбора области поиска и загрузки).</summary>
    Task<IReadOnlyList<CaseScopeItem>> ListAccessibleCasesAsync(AccessContext access, CancellationToken cancellationToken = default);

    /// <summary>Дело, если оно доступно субъекту; иначе <see langword="null"/>.</summary>
    Task<CaseScopeItem?> GetCaseAsync(int caseId, AccessContext access, CancellationToken cancellationToken = default);

    /// <summary>Основания поиска дела (поручения, постановления, ОРМ), если дело доступно; иначе пусто.</summary>
    Task<IReadOnlyList<CaseAuthorizationItem>> ListAuthorizationsAsync(int caseId, AccessContext access, CancellationToken cancellationToken = default);

    /// <summary>Идентификаторы носителей, привязанных к делам (область поиска для <c>FaceSearchQuery.AssetIds</c>).</summary>
    Task<IReadOnlyCollection<int>> GetAssetIdsAsync(IReadOnlyCollection<int> caseIds, CancellationToken cancellationToken = default);

    /// <summary>
    /// Дело носителя, ДОСТУПНОЕ субъекту (первая из привязок, прошедшая решётку и роль); <see langword="null"/> —
    /// носитель не привязан либо все его дела недоступны (неразличимо, ТБ-020/021). Носитель может быть
    /// привязан к нескольким делам (дедупликация по хешу) — чужие дела наружу не раскрываются.
    /// </summary>
    Task<int?> GetCaseIdForAssetAsync(int assetId, AccessContext access, CancellationToken cancellationToken = default);

    /// <summary>
    /// Входит ли носитель в дела субъекта (ТБ-071, ТФ-ДЕЛ-03): применяется ко ВСЕМ чтениям пакета по прямому
    /// идентификатору (носитель, лицо, файл, сессия по носителю) ПОВЕРХ floor ядра — иначе следователь
    /// того же подразделения перебором id читал бы материалы чужих дел, а субъект без роли — что угодно
    /// в допуске. Без роли — всегда <see langword="false"/> (default-deny, ТБ-012).
    /// </summary>
    Task<bool> IsAssetAccessibleAsync(int assetId, AccessContext access, CancellationToken cancellationToken = default);

    /// <summary>
    /// Место съёмки носителей (ТФ-ПЛ-02) — из привязки носителя к делу, только по делам, ДОСТУПНЫМ субъекту (роль и
    /// floor ядра, ТБ-012/021/071). Носитель без места или без доступной привязки в ответ не попадает; при нескольких
    /// привязках — место первой (по порядку привязки).
    /// </summary>
    Task<IReadOnlyDictionary<int, string>> ListAssetPlacesAsync(
        IReadOnlyCollection<int> assetIds, AccessContext access, CancellationToken cancellationToken = default);

    /// <summary>
    /// Можно ли СТРОИТЬ биометрию по этому носителю: <see langword="false"/>, если дело носителя закрыто
    /// и его шаблоны уже удалены регламентом (ТБ-074, ТФ-ДЕЛ-04, ADR-0024).
    /// </summary>
    /// <remarks>
    /// БЕЗ ЭТОЙ ПРОВЕРКИ РЕГЛАМЕНТ ОБХОДИТСЯ В ОДИН КЛИК: индексация носителя строит шаблоны заново, и
    /// биометрия закрытого дела возвращается в поиск — то есть снова обрабатывается без основания. Поэтому
    /// вопрос задаётся и вручную (повторная индексация оператором), и в фоновом конвейере (загрузка нового
    /// файла в закрытое дело).
    ///
    /// БЕЗ <see cref="AccessContext"/> намеренно: фоновый конвейер работает от имени системы, субъекта у него
    /// нет. Утечки сведений здесь тоже нет — ответ не выдаётся пользователю, он решает единственный вопрос
    /// «строить ли шаблоны». Носитель без дела (ещё не привязан) — <see langword="true"/>: запрета нет.
    /// Носитель, привязанный к нескольким делам, индексируется, пока ОТКРЫТО хотя бы одно: пока есть
    /// действующее основание, шаблоны правомерны.
    /// </remarks>
    Task<bool> IsBiometricIndexingAllowedAsync(int assetId, CancellationToken cancellationToken = default);

    /// <summary>Привязать носитель к делу (слабая ссылка по значению в схеме профиля, ТО-инф-08).</summary>
    Task LinkAssetAsync(int caseId, int assetId, string? place, int? linkedByUserId, CancellationToken cancellationToken = default);

    /// <summary>Фигуранты дела для привязки кандидата.</summary>
    Task<IReadOnlyList<CasePersonItem>> ListPersonsAsync(int caseId, AccessContext access, CancellationToken cancellationToken = default);

    /// <summary>
    /// Фигуранты дела, у которых это лицо уже подтверждено появлением (подсказка эксперту, ТФ-ВЕР-03); только
    /// видимые субъекту фигуранты и появления.
    /// </summary>
    Task<IReadOnlyCollection<int>> ListPersonsConfirmedOnFaceAsync(
        int caseId, int faceId, AccessContext access, CancellationToken cancellationToken = default);

    /// <summary>
    /// Зафиксировать подтверждённое появление фигуранта (ТФ-ВЕР-03 → ТФ-ПЕР-02). Лицо, уже подтверждённое у этого
    /// фигуранта, второй записи не создаёт.
    /// </summary>
    Task RecordAppearanceAsync(ConfirmedAppearance appearance, CancellationToken cancellationToken = default);

    /// <summary>
    /// Подтверждённые (не отозванные) появления фигурантов на носителе — отметки фигурантов на ленте видео (ADR-0038).
    /// Только фигуранты, доступные субъекту по полной решётке и роли, и появления под floor'ом (ТБ-020/021): недоступный
    /// фигурант не раскрывается ни именем, ни самим фактом отметки.
    /// </summary>
    Task<IReadOnlyList<AssetAppearanceItem>> ListAssetAppearancesAsync(
        int assetId, AccessContext access, CancellationToken cancellationToken = default);

    /// <summary>
    /// Дела только что проиндексированного носителя, в которых система предлагает связать найденные лица с
    /// фигурантами (ТФ-ПЕР-09): только ОТКРЫТЫЕ дела, у которых есть основание поиска (ТБ-071) и хотя бы один
    /// действующий эталон с лицом. Дело без основания в выдачу не попадает — без основания поиск невозможен.
    /// </summary>
    /// <remarks>
    /// БЕЗ <see cref="AccessContext"/> намеренно, как <see cref="IsBiometricIndexingAllowedAsync"/>: вызывает фоновый
    /// конвейер, субъекта у него нет. Наружу пользователю ответ не выдаётся; потолок, под которым система затем
    /// сравнивает лица, — режимные поля самого дела, а сопоставление идёт только с фигурантами ТОГО ЖЕ дела
    /// (Приложение В ТЗ, вопрос 20).
    /// </remarks>
    Task<IReadOnlyList<SuggestionTarget>> ListSuggestionTargetsAsync(int assetId, CancellationToken cancellationToken = default);

    /// <summary>
    /// То же правило для одного дела (ТФ-ПЕР-09): в деле появился эталон или основание — система сверяет с его
    /// фигурантами уже загруженные материалы. <see langword="null"/> — дело закрыто, нет основания или эталонов.
    /// </summary>
    /// <remarks>Без <see cref="AccessContext"/> по той же причине, что <see cref="ListSuggestionTargetsAsync"/>.</remarks>
    Task<SuggestionTarget?> GetSuggestionTargetAsync(int caseId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Готовность дел носителя к сверке (ТФ-ПЕР-09) — чтобы карточка носителя объяснила, почему система не
    /// предлагала фигурантов: дело закрыто, нет основания, нет эталонов. Только дела, ДОСТУПНЫЕ субъекту
    /// (роль и floor ядра, ТБ-012/021/071); чужие дела носителя наружу не раскрываются.
    /// </summary>
    Task<IReadOnlyList<SuggestionReadiness>> ListSuggestionReadinessAsync(
        int assetId, AccessContext access, CancellationToken cancellationToken = default);
}
