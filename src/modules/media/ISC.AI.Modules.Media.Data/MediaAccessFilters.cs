using ISC.AI.Abstractions.Security;

namespace ISC.AI.Modules.Media.Data;

/// <summary>
/// ЕДИНСТВЕННОЕ место, где записана решётка чтения пакета «Медиа» (ТБ-020/070): floor ядра
/// (<see cref="BaselineAccess"/>: гриф ≤ допуска И подразделение ∈ разрешённых) плюс сужающая политика
/// профиля (<see cref="IAccessPolicy"/>, ADR-0014). Применяется на стороне БД ко ВСЕМ режимным сущностям
/// схемы <c>media</c> — носителям, лицам, шаблонам, сессиям и кандидатам — одним и тем же способом, чтобы
/// каталог, история поисков и очередь верификации не могли разойтись в том, что видно субъекту.
/// </summary>
/// <remarks>
/// Fail-closed (ТБ-021): без контекста — <see cref="AccessContextRequiredException"/>, а не выборка без
/// фильтра; пустой список подразделений даёт пустую выборку по построению floor'а.
/// </remarks>
internal static class MediaAccessFilters
{
    /// <summary>Строки <typeparamref name="T"/>, видимые субъекту <paramref name="access"/>.</summary>
    internal static IQueryable<T> VisibleTo<T>(this IQueryable<T> source, AccessContext access, IAccessPolicy accessPolicy)
        where T : IClassified
    {
        if (access is null)
        {
            throw new AccessContextRequiredException();
        }

        return source
            .Where(BaselineAccess.Filter<T>(access))
            .Where(accessPolicy.BuildFilter<T>(access));
    }
}
