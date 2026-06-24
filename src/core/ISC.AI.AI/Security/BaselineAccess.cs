using System.Linq;
using System.Linq.Expressions;
using ISC.AI.Abstractions.Security;

namespace ISC.AI.AI.Security;

/// <summary>
/// Неизменяемый ядровой floor решётки доступа (ТБ-002/020/021). Применяется ретривером ВСЕГДА —
/// до доменной <see cref="IAccessPolicy"/> — и профилем не переопределяется. Гарантирует, что
/// материал выше допуска или вне разрешённых подразделений субъекта не выдаётся (опора GATE-1, ADR-0014).
/// </summary>
public static class BaselineAccess
{
    /// <summary>
    /// EF-транслируемый предикат floor'а: <c>гриф ≤ допуск И подразделение ∈ разрешённых</c>.
    /// Предикат строится по КОНКРЕТНОМУ типу <typeparamref name="T"/> (через <see cref="Expression.Property(Expression, string)"/>),
    /// а не через интерфейсный член — иначе EF Core не транслирует доступ к свойству в SQL.
    /// </summary>
    public static Expression<Func<T, bool>> Filter<T>(AccessContext subject) where T : IClassified
    {
        // fail-closed: без установленного субъекта с допуском фильтр не строится (ТБ-012/021).
        ArgumentNullException.ThrowIfNull(subject);

        var resource = Expression.Parameter(typeof(T), "r");

        // r.Classification <= subject.MaxClassification
        var classificationLeq = Expression.LessThanOrEqual(
            Expression.Property(resource, nameof(IClassified.Classification)),
            Expression.Constant(subject.MaxClassification));

        // subject.AllowedDivisions.Contains(r.DivisionId)  → SQL: divisionId = ANY(@divisions)
        var divisionAllowed = Expression.Call(
            typeof(Enumerable),
            nameof(Enumerable.Contains),
            [typeof(int)],
            Expression.Constant(subject.AllowedDivisions),
            Expression.Property(resource, nameof(IClassified.DivisionId)));

        var body = Expression.AndAlso(classificationLeq, divisionAllowed);
        return Expression.Lambda<Func<T, bool>>(body, resource);
    }
}
