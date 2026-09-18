namespace ISC.AI.Modules.Media.Domain.Model;

/// <summary>Область поиска по лицу (ТФ-ПЛ-05): что именно попросил субъект; фиксируется в аудите (ТБ-072).</summary>
public enum SearchScopeKind
{
    /// <summary>Только текущее дело (значение по умолчанию).</summary>
    CurrentCase = 1,

    /// <summary>Явно выбранные дела из доступных субъекту.</summary>
    SelectedCases = 2,

    /// <summary>Все дела, доступные субъекту по решётке и роли.</summary>
    AllAccessibleCases = 3,
}

/// <summary>
/// Состояние кандидата выдачи (ТБ-073, ДОК-13 §9). Ни один процесс не меняет его автоматически:
/// переходы — только решениями людей через <c>RecordVerificationCommand</c>.
/// </summary>
public enum CandidateStatus
{
    /// <summary>Выдан поиском; ждёт первичного разбора экспертом.</summary>
    Candidate = 0,

    /// <summary>Эксперт решение записал; ждёт слепого решения верификатора.</summary>
    PendingVerifier = 1,

    /// <summary>Два «подтверждён» от разных сотрудников — «следственная версия, требует процессуальной проверки».</summary>
    Confirmed = 2,

    /// <summary>Оба решения «отклонён».</summary>
    Rejected = 3,

    /// <summary>Расхождение либо «неопределённо» у любого из двоих — эскалация руководителю.</summary>
    Undetermined = 4,
}

/// <summary>Стадия двойной верификации (ТБ-073): эксперт → верификатор.</summary>
public enum VerificationStage
{
    /// <summary>Первичный разбор (эксперт по лицам).</summary>
    Expert = 1,

    /// <summary>Слепая вторая проверка (верификатор).</summary>
    Verifier = 2,
}

/// <summary>Исход решения одного сотрудника по кандидату.</summary>
public enum VerificationVerdict
{
    /// <summary>Подтверждён.</summary>
    Confirmed = 1,

    /// <summary>Отклонён.</summary>
    Rejected = 2,

    /// <summary>Неопределённо.</summary>
    Undetermined = 3,
}

/// <summary>Решение одного сотрудника по кандидату (ТБ-072: оба субъекта и время — в журнале).</summary>
/// <param name="SubjectId">Пользователь (core.app_user), принявший решение.</param>
/// <param name="Stage">Стадия.</param>
/// <param name="Verdict">Исход.</param>
/// <param name="Rationale">Обоснование (морфологические признаки по методике, ТФ-ВЕР-01).</param>
/// <param name="DecidedAtUtc">Когда.</param>
public sealed record VerificationDecision(
    int SubjectId,
    VerificationStage Stage,
    VerificationVerdict Verdict,
    string Rationale,
    DateTime DecidedAtUtc);

/// <summary>
/// ПРАВИЛО ДВУХ ЛИЦ (ТБ-073, GATE-5) — чистая функция без зависимостей. Живёт в модуле и профилем НЕ
/// переопределяется: профиль решает лишь, КТО вправе выступать экспертом или верификатором
/// (<see cref="Services.IVerificationPolicy"/>), но не может ослабить само правило.
/// </summary>
public static class TwoPersonRule
{
    /// <summary>Маркировка подтверждённого результата (ТБ-073): не «установлен», а версия для процессуальной проверки.</summary>
    public const string ConfirmedMarker = "следственная версия — требует процессуальной проверки";

    /// <summary>
    /// Статус кандидата по набору решений. Без решений — <see cref="CandidateStatus.Candidate"/>; только эксперт —
    /// <see cref="CandidateStatus.PendingVerifier"/> (каким бы ни был исход: верификатор смотрит слепо);
    /// оба «подтверждён» от РАЗНЫХ субъектов — <see cref="CandidateStatus.Confirmed"/>; оба «отклонён» —
    /// <see cref="CandidateStatus.Rejected"/>; иначе — <see cref="CandidateStatus.Undetermined"/>
    /// (наиболее консервативный вывод). Совпадение субъектов на двух стадиях — всегда «неопределённо»:
    /// самоподтверждение не даёт статуса даже если проскочило мимо валидатора.
    /// </summary>
    public static CandidateStatus Resolve(IReadOnlyList<VerificationDecision> decisions)
    {
        ArgumentNullException.ThrowIfNull(decisions);

        var expert = decisions.LastOrDefault(d => d.Stage == VerificationStage.Expert);
        var verifier = decisions.LastOrDefault(d => d.Stage == VerificationStage.Verifier);

        if (expert is null)
        {
            return CandidateStatus.Candidate;
        }

        if (verifier is null)
        {
            return CandidateStatus.PendingVerifier;
        }

        if (expert.SubjectId == verifier.SubjectId)
        {
            return CandidateStatus.Undetermined;
        }

        return (expert.Verdict, verifier.Verdict) switch
        {
            (VerificationVerdict.Confirmed, VerificationVerdict.Confirmed) => CandidateStatus.Confirmed,
            (VerificationVerdict.Rejected, VerificationVerdict.Rejected) => CandidateStatus.Rejected,
            _ => CandidateStatus.Undetermined,
        };
    }

    /// <summary>
    /// Может ли субъект записать решение стадии <paramref name="stage"/> при уже имеющихся решениях:
    /// эксперт — пока нет экспертного решения; верификатор — после эксперта, пока нет своего, и ТОЛЬКО
    /// другим субъектом (ТБ-073: одно лицо не может быть экспертом и верификатором одного результата).
    /// </summary>
    public static bool CanDecide(IReadOnlyList<VerificationDecision> decisions, VerificationStage stage, int subjectId, out string? reason)
    {
        ArgumentNullException.ThrowIfNull(decisions);
        reason = null;
        var expert = decisions.LastOrDefault(d => d.Stage == VerificationStage.Expert);
        var verifier = decisions.LastOrDefault(d => d.Stage == VerificationStage.Verifier);

        switch (stage)
        {
            case VerificationStage.Expert when expert is not null:
                reason = "Экспертное решение по кандидату уже записано.";
                return false;
            case VerificationStage.Verifier when expert is null:
                reason = "Верификация возможна только после решения эксперта.";
                return false;
            case VerificationStage.Verifier when verifier is not null:
                reason = "Решение верификатора по кандидату уже записано.";
                return false;
            case VerificationStage.Verifier when expert!.SubjectId == subjectId:
                reason = "Одно лицо не может быть экспертом и верификатором одного результата (ТБ-073).";
                return false;
            default:
                return true;
        }
    }
}
