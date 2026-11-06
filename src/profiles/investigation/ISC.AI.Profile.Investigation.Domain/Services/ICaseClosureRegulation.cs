namespace ISC.AI.Profile.Investigation.Domain.Services;

/// <summary>Итог регламента закрытия дела.</summary>
public enum CaseClosureRegulationOutcome
{
    /// <summary>Регламент не требуется: по настройке эксплуатанта шаблоны хранятся (ADR-0024).</summary>
    NotRequired = 0,

    /// <summary>Шаблоны лиц и вырезки дела сняты, акт закрытия записан.</summary>
    Executed = 1,

    /// <summary>Снять шаблоны не удалось (журнал аудита недоступен) — биометрия дела осталась.</summary>
    Failed = 2,
}

/// <summary>Итог регламента: исход, сколько шаблонов снято, причина сбоя.</summary>
/// <param name="Outcome">Исход.</param>
/// <param name="TemplatesRemoved">Снято шаблонов.</param>
/// <param name="Problem">Почему не исполнен — для оператора.</param>
public sealed record CaseClosureRegulationResult(CaseClosureRegulationOutcome Outcome, int TemplatesRemoved = 0, string? Problem = null);

/// <summary>
/// Регламент удаления биометрических шаблонов дела по закрытию (ТФ-ДЕЛ-04, ТБ-074, ADR-0024) — для закрытия, которое пришло
/// не сценарием «сменить статус», а из другого офиса: приёмом пакета обмена или решением Руководителя «принять версию
/// источника» (ADR-0068 v0.4). Порядок и смысл — те же, что у сценария смены статуса: статус уже «закрыто», затем снимаются
/// шаблоны, затем акт.
/// </summary>
/// <remarks>
/// ИНВАРИАНТ (ТБ-074): закрытие дела, пришедшее из другого офиса, не обходит регламент — иначе биометрия закрытого дела
/// осталась бы у получателя там, где эксплуатант требует её удалять. Удаление fail-closed: недоступен журнал аудита —
/// ни один шаблон не снят, исход «не исполнен», и оператору говорится прямо (повторное закрытие исполнит регламент).
/// </remarks>
public interface ICaseClosureRegulation
{
    /// <summary>Исполняет регламент для закрытого дела; настройка «хранить» — исход «не требуется».</summary>
    /// <param name="caseId">Дело (уже закрытое).</param>
    /// <param name="reason">Основание в журнал аудита.</param>
    /// <param name="subjectId">Кто инициировал.</param>
    /// <param name="cancellationToken">Отмена.</param>
    Task<CaseClosureRegulationResult> ExecuteAsync(int caseId, string reason, int? subjectId, CancellationToken cancellationToken = default);
}
