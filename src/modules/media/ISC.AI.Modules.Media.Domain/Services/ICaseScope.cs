using ISC.AI.Abstractions.Security;

namespace ISC.AI.Modules.Media.Domain.Services;

/// <summary>Дело, как его видит пакет «Медиа»: непрозрачный идентификатор, подпись и режимные поля.</summary>
/// <param name="CaseId">Идентификатор дела в схеме профиля.</param>
/// <param name="Number">Номер дела (для подписи).</param>
/// <param name="Title">Краткое название (для подписи).</param>
/// <param name="Classification">Гриф дела — наследуется носителями и шаблонами (ТБ-070).</param>
/// <param name="DivisionId">Подразделение дела.</param>
public sealed record CaseScopeItem(int CaseId, string Number, string Title, short Classification, int DivisionId);

/// <summary>Фигурант дела для привязки кандидата (ТФ-ВЕР-03); модуль знает только идентификатор и подпись.</summary>
public sealed record CasePersonItem(int PersonId, string DisplayName);

/// <summary>Основание поиска в деле (ТБ-071): непрозрачный идентификатор профиля и реквизиты для аудита.</summary>
public sealed record CaseAuthorizationItem(int AuthorizationId, string Reference);

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

    /// <summary>Зафиксировать подтверждённое появление фигуранта (ТФ-ВЕР-03 → ТФ-ПЕР-02).</summary>
    Task RecordAppearanceAsync(ConfirmedAppearance appearance, CancellationToken cancellationToken = default);
}
