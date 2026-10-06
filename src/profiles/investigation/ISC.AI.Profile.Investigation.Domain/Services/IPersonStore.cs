using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Investigation.Domain.Enums;

namespace ISC.AI.Profile.Investigation.Domain.Services;

/// <summary>
/// Анкета объекта (ТФ-ПЕР-05): все поля необязательны. При известной дате рождения год рождения равен её
/// году — это приводит хранилище, а валидатор отклоняет противоречивую пару.
/// </summary>
/// <param name="BirthDate">Дата рождения, если известна полностью.</param>
/// <param name="BirthYear">Год рождения (когда известен только год).</param>
/// <param name="BirthPlace">Место рождения.</param>
/// <param name="WorkPlace">Место работы.</param>
/// <param name="Residence">Место жительства.</param>
/// <param name="Sex">Пол.</param>
/// <param name="Alias">Псевдоним (оперативная кличка).</param>
public sealed record PersonQuestionnaire(
    DateOnly? BirthDate = null,
    int? BirthYear = null,
    string? BirthPlace = null,
    string? WorkPlace = null,
    string? Residence = null,
    PersonSex? Sex = null,
    string? Alias = null)
{
    /// <summary>Пустая анкета.</summary>
    public static PersonQuestionnaire Empty { get; } = new();
}

/// <summary>Черновик фигуранта (ТФ-ПЕР-01). Пустое имя при <paramref name="IsUnidentified"/> → «Неустановленное лицо № N».</summary>
/// <param name="CaseId">Дело.</param>
/// <param name="DisplayName">ФИО/установочные данные.</param>
/// <param name="IsUnidentified">Личность не установлена.</param>
/// <param name="RoleInCase">Уточнение роли свободным текстом.</param>
/// <param name="Notes">Примечания.</param>
/// <param name="Role">Роль по перечню (объект, связь, иная).</param>
/// <param name="Questionnaire">Анкета (ТФ-ПЕР-05); <see langword="null"/> — пустая.</param>
/// <param name="LinkedToPersonId">Чьей связью является (фигурант того же дела) — только у роли «связь» (ТФ-ПЕР-06).</param>
/// <param name="LinkTypeId">Кем приходится — запись справочника «тип связи» — только у роли «связь».</param>
public sealed record PersonDraft(
    int CaseId,
    string? DisplayName,
    bool IsUnidentified,
    string? RoleInCase,
    string? Notes,
    PersonRole Role = PersonRole.Other,
    PersonQuestionnaire? Questionnaire = null,
    int? LinkedToPersonId = null,
    int? LinkTypeId = null);

/// <summary>Фигурант в списке/карточке.</summary>
public sealed record PersonRow(
    int Id,
    int CaseId,
    string DisplayName,
    bool IsUnidentified,
    int? UnidentifiedNumber,
    string? RoleInCase,
    string? Notes,
    short Classification,
    int DivisionId,
    int ReferencePhotoCount,
    int AppearanceCount,
    PersonRole Role = PersonRole.Other,
    PersonQuestionnaire? Questionnaire = null,
    int? LinkedToPersonId = null,
    int? LinkTypeId = null);

/// <summary>
/// Подтверждённое появление (ТФ-ПЕР-02). Отрезок появления в видео (<c>TrackStartMs</c>..<c>TrackEndMs</c>, кадров —
/// <c>TrackFrames</c>) заполняет сценарий чтения по треку лица пакета «Медиа» (ADR-0037); хранилище его не знает.
/// </summary>
public sealed record AppearanceRow(
    int Id,
    int PersonId,
    int CaseId,
    int MediaAssetId,
    int MediaFaceId,
    int? FrameIndex,
    long? FrameTimestampMs,
    int SearchSessionId,
    int CandidateId,
    double Similarity,
    AppearanceStatus Status,
    DateTime ConfirmedAtUtc,
    int ExpertUserId,
    int VerifierUserId,
    DateTime? RevokedAtUtc = null,
    int? RevokedByUserId = null,
    string? RevokeReason = null,
    long? TrackStartMs = null,
    long? TrackEndMs = null,
    int? TrackFrames = null)
{
    /// <summary>Появление отозвано как ошибочное.</summary>
    public bool IsRevoked => Status == AppearanceStatus.Revoked;
}

/// <summary>Итог отзыва появления (ADR-0034).</summary>
public enum AppearanceRevokeResult
{
    /// <summary>Отозвано.</summary>
    Ok = 0,

    /// <summary>Появление не найдено или недоступно (неотличимо, ТБ-021).</summary>
    NotFound = 1,

    /// <summary>Уже отозвано.</summary>
    AlreadyRevoked = 2,

    /// <summary>Отзывающий сам подтверждал это появление (эксперт или верификатор) — нужен другой сотрудник.</summary>
    OwnDecision = 3,
}

/// <summary>Действующее появление фигуранта на носителе — для отметок на ленте видео (ADR-0038).</summary>
/// <param name="PersonId">Фигурант.</param>
/// <param name="DisplayName">Подпись фигуранта.</param>
/// <param name="FaceId">Лицо носителя, как его запомнило появление.</param>
/// <param name="FrameTimestampMs">Момент кадра лица, мс; у фото — <see langword="null"/>.</param>
public sealed record AssetAppearanceRow(int PersonId, string DisplayName, int FaceId, long? FrameTimestampMs);

/// <summary>Черновик появления — из подтверждённого кандидата модуля «Медиа».</summary>
public sealed record AppearanceDraft(
    int PersonId,
    int CaseId,
    int MediaAssetId,
    int MediaFaceId,
    int? FrameIndex,
    long? FrameTimestampMs,
    int SearchSessionId,
    int CandidateId,
    double Similarity,
    DateTime ConfirmedAtUtc,
    int ExpertUserId,
    int VerifierUserId,
    short Classification,
    int DivisionId);

/// <summary>Эталон фигуранта (ТФ-ПЕР-01, ТБ-077).</summary>
public sealed record ReferencePhotoRow(
    int Id, int PersonId, int MediaAssetId, int? MediaFaceId, float? QualityScore, string? Source,
    string? LegalBasis, DateOnly? ReviewDueAt, int? AddedByUserId, int? SupersededById, DateTime CreatedAt);

/// <summary>Черновик эталона.</summary>
public sealed record ReferencePhotoDraft(
    int PersonId, int MediaAssetId, int? MediaFaceId, float? QualityScore, string? Source,
    string? LegalBasis, DateOnly? ReviewDueAt, int? AddedByUserId);

/// <summary>Исход записи фигуранта.</summary>
public enum PersonWriteResult
{
    /// <summary>Успех.</summary>
    Ok = 0,

    /// <summary>Фигурант/дело не найдены или недоступны.</summary>
    NotFound = 1,

    /// <summary>
    /// Связь не согласована (ТФ-ПЕР-06): поля связи у роли, отличной от «связь»; фигурант, с которым связь,
    /// не из этого дела или это он сам; тип связи — не запись справочника «тип связи» или выключен.
    /// </summary>
    InvalidLink = 2,
}

/// <summary>Хранилище фигурантов, эталонов и появлений (ТФ-ПЕР-01/02). Чтение — под решёткой; гриф/подразделение берутся у дела.</summary>
public interface IPersonStore
{
    /// <summary>Фигуранты дела.</summary>
    Task<IReadOnlyList<PersonRow>> ListByCaseAsync(int caseId, AccessContext access, CancellationToken cancellationToken = default);

    /// <summary>Фигурант, если доступен.</summary>
    Task<PersonRow?> GetAsync(int personId, AccessContext access, CancellationToken cancellationToken = default);

    /// <summary>Создать фигуранта в деле (дело должно быть доступно).</summary>
    Task<(PersonWriteResult Result, int PersonId)> CreateAsync(PersonDraft draft, AccessContext access, CancellationToken cancellationToken = default);

    /// <summary>
    /// Изменить реквизиты, роль и анкету — заменяются целиком. Дело, гриф и подразделение не меняются:
    /// <see cref="PersonDraft.CaseId"/> черновика правки игнорируется.
    /// </summary>
    Task<PersonWriteResult> UpdateAsync(int personId, PersonDraft edit, AccessContext access, CancellationToken cancellationToken = default);

    /// <summary>Появления фигуранта (подтверждённые двумя лицами, ТБ-073, включая отозванные — с пометкой), новые первыми.</summary>
    Task<IReadOnlyList<AppearanceRow>> ListAppearancesAsync(int personId, AccessContext access, CancellationToken cancellationToken = default);

    /// <summary>
    /// Записать появление (вызывается модулем «Медиа» через порт после подтверждения; без решётки — факт уже проверен).
    /// Одно появление на пару «фигурант — лицо»: если это лицо у фигуранта уже подтверждено (другой сессией поиска),
    /// новая запись не создаётся — возвращается существующая.
    /// </summary>
    Task<int> AddAppearanceAsync(AppearanceDraft draft, CancellationToken cancellationToken = default);

    /// <summary>
    /// Отозвать ошибочное появление (ADR-0034): статус «отозвано», кто, когда и почему. Появление должно быть видно
    /// субъекту так же, как в <see cref="ListAppearancesAsync"/>; отзывает не эксперт и не верификатор этого появления.
    /// </summary>
    Task<AppearanceRevokeResult> RevokeAppearanceAsync(
        int appearanceId, string reason, AccessContext access, CancellationToken cancellationToken = default);

    /// <summary>
    /// Фигуранты дела, у которых это лицо уже подтверждено появлением, — подсказка эксперту «уже подтверждено у …»
    /// (ТФ-ВЕР-03). Только фигуранты, доступные субъекту по полной решётке, и появления под floor'ом.
    /// </summary>
    Task<IReadOnlyCollection<int>> ListPersonsConfirmedOnFaceAsync(
        int caseId, int faceId, AccessContext access, CancellationToken cancellationToken = default);

    /// <summary>
    /// Действующие (не отозванные) появления фигурантов на носителе — для отметок на ленте видео (ADR-0038). Только
    /// фигуранты, доступные субъекту по полной решётке и роли, и появления под floor'ом (ТБ-020/021).
    /// </summary>
    Task<IReadOnlyList<AssetAppearanceRow>> ListAppearancesOnAssetAsync(
        int assetId, AccessContext access, CancellationToken cancellationToken = default);

    /// <summary>Эталоны фигуранта.</summary>
    Task<IReadOnlyList<ReferencePhotoRow>> ListReferencePhotosAsync(int personId, AccessContext access, CancellationToken cancellationToken = default);

    /// <summary>Добавить эталон; прежний актуальный (если <paramref name="supersedesId"/>) помечается заменённым, но не удаляется (ТБ-077).</summary>
    Task<(PersonWriteResult Result, int PhotoId)> AddReferencePhotoAsync(ReferencePhotoDraft draft, int? supersedesId, AccessContext access, CancellationToken cancellationToken = default);
}
