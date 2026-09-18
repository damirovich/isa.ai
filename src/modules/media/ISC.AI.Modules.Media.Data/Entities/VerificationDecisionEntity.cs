using ISC.AI.Abstractions.Entities;
using ISC.AI.Modules.Media.Domain.Model;

namespace ISC.AI.Modules.Media.Data.Entities;

/// <summary>
/// Решение одного сотрудника по кандидату (<c>media.verification_decision</c>, ТБ-073, ТФ-ВЕР-01/02):
/// стадия, исход, обоснование. На стадию — не более одного решения (уникальный индекс): второе решение
/// той же стадии — нарушение правила двух лиц, отклоняется базой даже в обход валидатора. Субъект —
/// слабая ссылка на <c>core.app_user</c> (ТО-инф-08). Каскадно удаляется с кандидатом.
/// </summary>
public class VerificationDecisionEntity : BaseEntity
{
    /// <summary>Кандидат.</summary>
    public int CandidateId { get; set; }

    /// <summary>Пользователь, принявший решение.</summary>
    public int SubjectId { get; set; }

    /// <summary>Стадия (эксперт / верификатор).</summary>
    public VerificationStage Stage { get; set; }

    /// <summary>Исход.</summary>
    public VerificationVerdict Verdict { get; set; }

    /// <summary>Обоснование по методике (морфологические признаки, ТФ-ВЕР-01).</summary>
    public required string Rationale { get; set; }

    /// <summary>Когда принято (UTC).</summary>
    public DateTime DecidedAtUtc { get; set; }
}
