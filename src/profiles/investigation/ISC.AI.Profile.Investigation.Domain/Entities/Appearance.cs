using ISC.AI.Abstractions.Entities;
using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Investigation.Domain.Enums;

namespace ISC.AI.Profile.Investigation.Domain.Entities;

/// <summary>
/// Подтверждённое появление фигуранта в носителе (<c>investigation.appearance</c>, ТФ-ПЕР-02, ТФ-ВЕР-03):
/// создаётся ТОЛЬКО после двух «подтверждён» разных сотрудников (ТБ-073) и всегда имеет статус
/// «следственная версия». Ссылки на носитель/лицо/сессию/кандидата — по значению в схему <c>media</c>.
/// </summary>
/// <remarks>
/// ИНВАРИАНТ ПРОИСХОЖДЕНИЯ (ТБ-073, ADR-0068 v0.3; держит ограничение таблицы <c>ck_appearance_origin</c>): локальное
/// появление — только из подтверждённого кандидата: заданы <see cref="SearchSessionId"/>, <see cref="CandidateId"/> и
/// <see cref="ExpertUserId"/>. Без них — только появление, перенесённое из другого офиса (ТФ-ОБМ-02): оно несёт пометку
/// <see cref="SourceInstance"/> и подписи и решения источника (<see cref="SourceProvenance"/>) — это не решение получателя.
/// </remarks>
public class Appearance : AuditableEntity, IClassified
{
    /// <summary>Фигурант (FK внутри схемы).</summary>
    public int PersonId { get; set; }

    /// <summary>Навигация к фигуранту.</summary>
    public Person? Person { get; set; }

    /// <summary>Дело (денормализовано для списков по делу).</summary>
    public int CaseId { get; set; }

    /// <summary>Носитель (по значению → media.asset).</summary>
    public int MediaAssetId { get; set; }

    /// <summary>Лицо (по значению → media.face).</summary>
    public int MediaFaceId { get; set; }

    /// <summary>Индекс кадра (видео).</summary>
    public int? FrameIndex { get; set; }

    /// <summary>Таймкод кадра, мс (видео) — переход к кадру (ТФ-ПЕР-02).</summary>
    public long? FrameTimestampMs { get; set; }

    /// <summary>
    /// Поисковая сессия (по значению → media.search_session); у появления из другого офиса — <see langword="null"/>.
    /// </summary>
    public int? SearchSessionId { get; set; }

    /// <summary>
    /// Кандидат (по значению → media.search_candidate); у появления из другого офиса — <see langword="null"/>.
    /// </summary>
    public int? CandidateId { get; set; }

    /// <summary>Косинусная схожесть на момент подтверждения (привязана к версии модели, ТО-мат-09).</summary>
    public double Similarity { get; set; }

    /// <summary>Статус — «следственная версия» при создании; «отозвано» — после отзыва (ADR-0034).</summary>
    public AppearanceStatus Status { get; set; } = AppearanceStatus.InvestigativeLead;

    /// <summary>Когда подтверждено (UTC).</summary>
    public DateTime ConfirmedAtUtc { get; set; }

    /// <summary>
    /// Первая подпись (слабая ссылка на пользователя ядра): эксперт, а в режиме одного оператора (ADR-0054) — тот, кто
    /// подтвердил (оператор или руководитель по «неопределённому»). У появления из другого офиса — учётка получателя с
    /// тем же именем входа или <see langword="null"/>; подписи источника — в <see cref="SourceProvenance"/>.
    /// </summary>
    public int? ExpertUserId { get; set; }

    /// <summary>
    /// Вторая подпись в режиме двух лиц — верификатор или руководитель (слабая ссылка; всегда ≠ эксперт, ТБ-073);
    /// в режиме одного оператора — <see langword="null"/>: подпись одна (ТФ-ВЕР-07, ADR-0054).
    /// </summary>
    public int? VerifierUserId { get; set; }

    /// <summary>Гриф. NOT NULL.</summary>
    public short Classification { get; set; }

    /// <summary>Подразделение. NOT NULL.</summary>
    public int DivisionId { get; set; }

    /// <summary>Когда отозвано (UTC); <see langword="null"/> — действует.</summary>
    public DateTime? RevokedAtUtc { get; set; }

    /// <summary>Кто отозвал (слабая ссылка; всегда ≠ эксперт и ≠ верификатор этого появления, ADR-0034).</summary>
    public int? RevokedByUserId { get; set; }

    /// <summary>Причина отзыва — обязательна.</summary>
    public string? RevokeReason { get; set; }

    /// <summary>
    /// Пометка «импортировано из …» (ТФ-ОБМ-02, ADR-0068 v0.3): номер экземпляра-источника; <see langword="null"/> —
    /// появление подтверждено здесь.
    /// </summary>
    public string? SourceInstance { get; set; }

    /// <summary>
    /// Провенанс появления из другого офиса, JSON (<see cref="Exchange.AppearanceProvenance"/>): пакет, номер в источнике,
    /// подписи и решения всех этапов с именами сотрудников источника.
    /// </summary>
    public string? SourceProvenance { get; set; }

    /// <summary>Появление перенесено из другого офиса — решения источника, а не получателя (ТБ-073).</summary>
    public bool IsImported => SourceInstance is not null;
}
