using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Investigation.Domain.Entities;
using ISC.AI.Profile.Investigation.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ISC.AI.Profile.Investigation.Data;

/// <summary>
/// Единое правило видимости фигуранта (ТБ-020/021, ТБ-070) для всех хранилищ профиля: floor ядра и политика
/// профиля — на самом фигуранте (у него свои режимные поля), сужение по роли и владению — через дело
/// (<see cref="CaseAccessRule"/>): фигурант виден ровно тогда, когда видно его дело. Одно место — чтобы
/// фигуранты, их адреса и транспорт не разошлись в том, кому они видны.
/// </summary>
internal static class PersonAccess
{
    /// <summary>Фигуранты, доступные субъекту.</summary>
    /// <remarks>
    /// Подзапрос по делам НАРОЧНО без <c>AsNoTracking()</c>: EF Core применяет <c>AsNoTracking</c>, встреченный
    /// в любом месте дерева выражения, ко ВСЕМУ запросу — и отслеживаемая правка фигуранта молча терялась.
    /// Дела внутри <c>Any</c> не материализуются, отслеживать нечего; режим отслеживания задаётся только на
    /// корне (фигуранты).
    /// </remarks>
    public static IQueryable<Person> Accessible(
        InvestigationDbContext db, AccessContext access, IAccessPolicy policy, InvestigationRole? role, bool tracking = false)
    {
        var cases = CaseAccessRule.Apply(db.Cases, access, policy, role);
        var persons = tracking ? db.Persons : db.Persons.AsNoTracking();

        return persons
            .Where(BaselineAccess.Filter<Person>(access))
            .Where(policy.BuildFilter<Person>(access))
            .Where(p => cases.Any(c => c.Id == p.CaseId));
    }
}
