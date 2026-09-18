using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.DocFlow.Domain.Entities;

namespace ISC.AI.Modules.DocFlow.Data;

/// <summary>
/// ЕДИНСТВЕННОЕ место, где записан предикат видимости документа: решётка «гриф ≤ допуска И
/// подразделение ∈ разрешённых» (ТБ-020/021) плюс сужающая политика профиля (ADR-0014).
/// </summary>
/// <remarks>
/// До 2026-08-10 предикат существовал в ЧЕТЫРЁХ копиях — список документов, отчёты, дашборд,
/// уведомления — с комментарием «дублируется намеренно» и тестами, стерегущими равенство копий
/// («отчёт не выдаёт того, чего не выдаёт список»). Комментарий устарел в момент, когда копий стало
/// больше одной пары: разойдись любая из них — утечка пошла бы через отчёт или счётчик дашборда,
/// где её труднее всего заметить. Теперь расхождение невозможно структурно; тесты равенства остаются
/// как страховка уже этого метода.
///
/// Fail-closed по построению: пустой список разрешённых подразделений даёт пустую выборку, а не «все».
///
/// Решётка обязана СОВПАДАТЬ с floor'ом ядра (<see cref="BaselineAccess"/>) и проверкой раздачи
/// файлов (этап 4.3) — это один и тот же инвариант в трёх слоях. Исторически предикат здесь
/// написан прямо в LINQ: floor жил в <c>ISC.AI.AI</c>, на который модуль ссылаться не вправе.
/// С переносом floor'а в <c>Abstractions</c> (ADR-0018) дублирование стало необязательным — переход
/// на <c>BaselineAccess.Filter&lt;Document&gt;</c> отдельным шагом (поведение не меняется, тесты
/// равенства его и подтвердят). Сужающая политика (второй Where) — построчные правила профиля
/// по роли/владению (§2.1 ТЗ СКИД, ADR-0014): без профиля с политикой <c>BuildFilter</c>
/// пропускает всё без изменений.
/// </remarks>
internal static class AccessFilterExtensions
{
    /// <summary>Документы, видимые субъекту <paramref name="access"/>.</summary>
    internal static IQueryable<Document> VisibleTo(
        this IQueryable<Document> documents, AccessContext access, IAccessPolicy accessPolicy)
    {
        var allowedDivisions = access.AllowedDivisions;
        return documents
            .Where(d => d.Classification <= access.MaxClassification
                && allowedDivisions.Contains(d.DivisionId))
            .Where(accessPolicy.BuildFilter<Document>(access));
    }
}
