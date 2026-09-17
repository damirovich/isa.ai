using ISC.AI.Abstractions.Entities;
using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Investigation.Domain.Enums;

namespace ISC.AI.Profile.Investigation.Domain.Entities;

/// <summary>
/// Подтверждённое появление фигуранта в носителе (<c>investigation.appearance</c>, ТФ-ПЕР-02, ТФ-ВЕР-03):
/// создаётся ТОЛЬКО после двух «подтверждён» разных сотрудников (ТБ-073) и всегда имеет статус
/// «следственная версия». Ссылки на носитель/лицо/сессию/кандидата — по значению в схему <c>media</c>.
/// </summary>
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

    /// <summary>Поисковая сессия (по значению → media.search_session).</summary>
    public int SearchSessionId { get; set; }

    /// <summary>Кандидат (по значению → media.search_candidate).</summary>
    public int CandidateId { get; set; }

    /// <summary>Косинусная схожесть на момент подтверждения (привязана к версии модели, ТО-мат-09).</summary>
    public double Similarity { get; set; }

    /// <summary>Статус — всегда «следственная версия».</summary>
    public AppearanceStatus Status { get; set; } = AppearanceStatus.InvestigativeLead;

    /// <summary>Когда подтверждено (UTC).</summary>
    public DateTime ConfirmedAtUtc { get; set; }

    /// <summary>Эксперт (слабая ссылка на пользователя ядра).</summary>
    public int ExpertUserId { get; set; }

    /// <summary>Верификатор (слабая ссылка; всегда ≠ эксперт, ТБ-073).</summary>
    public int VerifierUserId { get; set; }

    /// <summary>Гриф. NOT NULL.</summary>
    public short Classification { get; set; }

    /// <summary>Подразделение. NOT NULL.</summary>
    public int DivisionId { get; set; }
}
