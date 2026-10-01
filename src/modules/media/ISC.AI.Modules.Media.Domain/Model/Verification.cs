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
/// Происхождение поисковой сессии: поиск оператора (или ручная привязка лица) либо автоматическое предложение
/// системы после индексации нового носителя (ТФ-ПЕР-09). Происхождение не меняет правил: кандидат системы
/// проходит ту же двойную верификацию людьми (ТБ-073) — система ничего не подтверждает.
/// </summary>
public enum SessionOrigin
{
    /// <summary>Поиск или привязка, запущенные сотрудником.</summary>
    Operator = 1,

    /// <summary>Предложено системой: лица нового носителя сопоставлены с эталонами фигурантов того же дела.</summary>
    SystemSuggestion = 2,
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

/// <summary>Стадия двойной верификации (ТБ-073): эксперт → верификатор; при расхождении — руководитель (ТФ-ВЕР-02).</summary>
public enum VerificationStage
{
    /// <summary>Первичный разбор (эксперт по лицам).</summary>
    Expert = 1,

    /// <summary>Слепая вторая проверка (верификатор).</summary>
    Verifier = 2,

    /// <summary>
    /// Решение руководителя по «неопределённому» кандидату (ТФ-ВЕР-02, ADR-0036): видит оба решения и выносит итог —
    /// подтвердить (только вместе с положительным решением другого сотрудника, ТБ-073) или отклонить.
    /// </summary>
    Supervisor = 3,
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
/// переопределяется: профиль решает лишь, КТО вправе выступать экспертом, верификатором или руководителем
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
    /// <remarks>
    /// РЕШЕНИЕ РУКОВОДИТЕЛЯ (ТФ-ВЕР-02, ADR-0036) учитывается только у «неопределённого» кандидата и только от
    /// третьего лица (не эксперта и не верификатора): «отклонён» — <see cref="CandidateStatus.Rejected"/>;
    /// «подтверждён» — <see cref="CandidateStatus.Confirmed"/>, ТОЛЬКО если кто-то другой из двоих уже сказал
    /// «подтверждён» — так у результата по-прежнему два независимых положительных решения разных сотрудников
    /// (ТБ-073). Иначе — остаётся «неопределённо»: одно положительное решение статуса не даёт ни при какой роли.
    /// </remarks>
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

        var pair = expert.SubjectId == verifier.SubjectId
            ? CandidateStatus.Undetermined
            : (expert.Verdict, verifier.Verdict) switch
            {
                (VerificationVerdict.Confirmed, VerificationVerdict.Confirmed) => CandidateStatus.Confirmed,
                (VerificationVerdict.Rejected, VerificationVerdict.Rejected) => CandidateStatus.Rejected,
                _ => CandidateStatus.Undetermined,
            };

        var supervisor = decisions.LastOrDefault(d => d.Stage == VerificationStage.Supervisor);
        if (pair != CandidateStatus.Undetermined || supervisor is null
            || supervisor.SubjectId == expert.SubjectId || supervisor.SubjectId == verifier.SubjectId)
        {
            return pair;
        }

        return supervisor.Verdict switch
        {
            VerificationVerdict.Rejected => CandidateStatus.Rejected,
            VerificationVerdict.Confirmed when PositiveBy(expert, verifier, exceptSubjectId: supervisor.SubjectId) is not null
                => CandidateStatus.Confirmed,
            _ => CandidateStatus.Undetermined,
        };
    }

    /// <summary>
    /// Положительное решение эксперта или верификатора, принятое НЕ субъектом <paramref name="exceptSubjectId"/>: вторая
    /// подпись под «подтверждён» руководителя (ТБ-073). Сначала эксперт, затем верификатор; нет — <see langword="null"/>.
    /// </summary>
    public static VerificationDecision? PositiveBy(VerificationDecision? expert, VerificationDecision? verifier, int exceptSubjectId) =>
        expert is { Verdict: VerificationVerdict.Confirmed } e && e.SubjectId != exceptSubjectId ? e
        : verifier is { Verdict: VerificationVerdict.Confirmed } v && v.SubjectId != exceptSubjectId ? v
        : null;

    /// <summary>
    /// Может ли субъект записать решение стадии <paramref name="stage"/> при уже имеющихся решениях:
    /// эксперт — пока нет экспертного решения; верификатор — после эксперта, пока нет своего, и ТОЛЬКО
    /// другим субъектом (ТБ-073: одно лицо не может быть экспертом и верификатором одного результата);
    /// руководитель — только по «неопределённому» кандидату, один раз и только третьим лицом (ТФ-ВЕР-02).
    /// </summary>
    public static bool CanDecide(IReadOnlyList<VerificationDecision> decisions, VerificationStage stage, int subjectId, out string? reason)
    {
        ArgumentNullException.ThrowIfNull(decisions);
        reason = null;
        var expert = decisions.LastOrDefault(d => d.Stage == VerificationStage.Expert);
        var verifier = decisions.LastOrDefault(d => d.Stage == VerificationStage.Verifier);
        var supervisor = decisions.LastOrDefault(d => d.Stage == VerificationStage.Supervisor);

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
            case VerificationStage.Supervisor when supervisor is not null:
                reason = "Решение руководителя по кандидату уже записано.";
                return false;
            case VerificationStage.Supervisor when Resolve(decisions) != CandidateStatus.Undetermined:
                reason = "Руководитель решает только по кандидату со статусом «неопределённо» — после решений эксперта и верификатора.";
                return false;
            case VerificationStage.Supervisor when expert!.SubjectId == subjectId || verifier!.SubjectId == subjectId:
                reason = "Итог по расхождению выносит третье лицо: вы уже принимали решение по этому кандидату (ТБ-073).";
                return false;
            case VerificationStage.Expert or VerificationStage.Verifier or VerificationStage.Supervisor:
                return true;
            default:
                reason = "Неизвестная стадия верификации.";
                return false;
        }
    }

    /// <summary>
    /// Можно ли руководителю записать именно этот исход (ТБ-073, ADR-0036): «отклонён» — всегда; «подтверждён» — только
    /// при положительном решении эксперта или верификатора другого сотрудника (вторая подпись); «неопределённо» —
    /// нельзя: руководитель снимает неопределённость, а не оставляет её.
    /// </summary>
    public static bool CanSupervisorDecide(
        IReadOnlyList<VerificationDecision> decisions, VerificationVerdict verdict, int subjectId, out string? reason)
    {
        ArgumentNullException.ThrowIfNull(decisions);
        reason = null;
        switch (verdict)
        {
            case VerificationVerdict.Rejected:
                return true;
            case VerificationVerdict.Confirmed when PositiveBy(
                decisions.LastOrDefault(d => d.Stage == VerificationStage.Expert),
                decisions.LastOrDefault(d => d.Stage == VerificationStage.Verifier),
                subjectId) is not null:
                return true;
            case VerificationVerdict.Confirmed:
                reason = "Подтвердить можно, только если эксперт или верификатор сказал «подтверждён»: статус требует двух "
                    + "положительных решений разных сотрудников (ТБ-073). Здесь возможно только «отклонён».";
                return false;
            default:
                reason = "Руководитель выносит итог: «подтверждён» или «отклонён».";
                return false;
        }
    }
}
