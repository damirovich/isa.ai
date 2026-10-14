using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Investigation.Domain.Entities;
using ISC.AI.Profile.Investigation.Domain.Enums;

namespace ISC.AI.Profile.Investigation.Data;

/// <summary>
/// ЕДИНСТВЕННОЕ место правила доступа к делам профиля «Следствие» (ТБ-071: область — дела субъекта;
/// ТФ-ДЕЛ-03: руководитель — дела подразделения). Применяется ПОВЕРХ floor'а ядра
/// (<see cref="BaselineAccess"/>: гриф ≤ допуск И подразделение ∈ разрешённых, ТБ-020/021) и профильной
/// <see cref="IAccessPolicy"/> — то есть только СУЖАЕТ. Хранилища дел и фигурантов вызывают
/// <see cref="Apply"/>, а не повторяют условия у себя.
/// </summary>
public static class CaseAccessRule
{
    /// <summary>
    /// Сужение по роли и владению. Администратор, Руководитель, Офицер ИБ, Эксперт по лицам и
    /// Верификатор — без сужения (floor подразделения/грифа уже применён вызывающим: руководитель видит
    /// дела СВОИХ подразделений, потому что чужие отсёк допуск). Следователь — только дела, где он
    /// ведущий или автор записи. Без роли — ПУСТО (default-deny, ТБ-012/021: новая учётка ничего не
    /// видит, пока Администратор не назначит роль).
    /// </summary>
    public static IQueryable<CaseFile> Narrow(IQueryable<CaseFile> cases, InvestigationRole? role, int? userId)
    {
        ArgumentNullException.ThrowIfNull(cases);

        return role switch
        {
            InvestigationRole.Administrator
                or InvestigationRole.Head
                or InvestigationRole.SecurityOfficer
                or InvestigationRole.FaceExpert
                or InvestigationRole.Verifier => cases,
            InvestigationRole.Investigator when userId is { } me =>
                cases.Where(c => c.InvestigatorUserId == me || c.CreatedByUserId == me),
            _ => cases.Where(_ => false),
        };
    }

    /// <summary>
    /// Полная цепочка «floor ядра → политика профиля → сужение по роли» над делами. Все три звена
    /// транслируются в SQL — решётка работает на стороне БД (ТБ-020), недоступное дело неотличимо
    /// от несуществующего (ТБ-021).
    /// </summary>
    public static IQueryable<CaseFile> Apply(
        IQueryable<CaseFile> cases, AccessContext access, IAccessPolicy policy, InvestigationRole? role) =>
        Narrow(WithinClearance(cases, access, policy), role, access.NumericSubjectId);

    /// <summary>
    /// Дела В ПРЕДЕЛАХ ДОПУСКА: floor ядра и политика профиля БЕЗ сужения по роли и владению. Только для путей, где
    /// это решено отдельно: взаимный поиск по лицу ОН+ОУ и материалы его кандидатов (ADR-0044). Floor не ослабляется:
    /// дело выше допуска или чужого подразделения (в том числе ОТМ без допуска к нему, ТС-014) сюда не попадает.
    /// Наличие роли проверяет вызывающий — без роли эти пути пусты (default-deny, ТБ-012).
    /// </summary>
    public static IQueryable<CaseFile> WithinClearance(IQueryable<CaseFile> cases, AccessContext access, IAccessPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(cases);
        ArgumentNullException.ThrowIfNull(access);
        ArgumentNullException.ThrowIfNull(policy);

        return cases
            .Where(BaselineAccess.Filter<CaseFile>(access))
            .Where(policy.BuildFilter<CaseFile>(access));
    }
}
