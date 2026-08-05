namespace ISC.AI.Modules.DocFlow.Domain.Services;

/// <summary>Строка справочника подразделений для выбора в UI.</summary>
public sealed record DivisionItem(int Id, string Name);

/// <summary>
/// Порт справочника подразделений (вопрос 3 Э4-35). Словарь идентификаторов ЕДИН для всей системы —
/// тот, которым живёт решётка доступа ядра (<c>core.clearance</c> / <c>AccessContext.AllowedDivisions</c>);
/// сам справочник ведёт ПРОФИЛЬ (у «Инспектора» — иерархия <c>inspector.division</c>) и отдаёт модулю
/// реализацию через DI — та же инверсия, что <c>IAccessPolicy</c> (ADR-0014). Модуль своего справочника
/// не заводит.
/// </summary>
public interface IDivisionDirectory
{
    /// <summary>Список подразделений для выбора (идентификатор + наименование), по алфавиту.</summary>
    Task<IReadOnlyList<DivisionItem>> ListAsync(CancellationToken cancellationToken = default);
}
