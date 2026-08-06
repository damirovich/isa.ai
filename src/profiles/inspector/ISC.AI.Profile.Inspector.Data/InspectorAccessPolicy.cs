using System.Linq.Expressions;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.DocFlow.Domain.Entities;
using ISC.AI.Profile.Inspector.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ISC.AI.Profile.Inspector.Data;

/// <summary>
/// Построчные правила доступа профиля «ИнспекторAI» (§2.1 ТЗ СКИД, этап 6 Э4-35) — СУЖАЮЩАЯ политика
/// поверх floor'а ядра (ADR-0014): floor уже отсеял гриф/подразделение (ТБ-020/021), эта политика
/// дополнительно сужает по РОЛИ и ВЛАДЕНИЮ. Переопределяет <c>AllowAllAccessPolicy</c> ядра через
/// повторную регистрацию <c>IAccessPolicy</c> в контейнере (см. <c>AddInspectorPersistence</c>) — тот
/// же механизм override, что и у <c>ICitationExtractor</c>/<c>ICitationNormalizer</c> (только явная
/// подмена дефолта, не вторая параллельная регистрация).
/// </summary>
/// <remarks>
/// Правило действует ТОЛЬКО для <see cref="Document"/> — докфлоу-специфичное понятие «роль» не должно
/// сужать RAG-поиск по всему корпусу (НПА и т.п.): для любого другого <c>T</c> (в частности
/// <c>EmbeddingEntity</c>, единственный сегодняшний вызывающий помимо докфлоу — <c>PgVectorRetriever</c>)
/// метод возвращает разрешающее <c>_ =&gt; true</c> без обращения к БД. Приведение результата к
/// <c>Expression&lt;Func&lt;T, bool&gt;&gt;</c> безопасно ТОЛЬКО потому, что оно происходит в ветке,
/// проверившей <c>typeof(T) == typeof(Document)</c> — в момент выполнения фактический тип выражения
/// уже <c>Expression&lt;Func&lt;Document, bool&gt;&gt;</c>.
///
/// Без назначенной роли (новая JIT-учётка после первого входа, ролью ещё никто не наделил) — DEFAULT-DENY
/// (ТБ-012, тот же принцип, что у грифа): документов не видно, пока администратор явно не назначит роль.
/// </remarks>
public sealed class InspectorAccessPolicy(IDbContextFactory<InspectorDbContext> contextFactory) : IAccessPolicy
{
    /// <inheritdoc />
    public Expression<Func<T, bool>> BuildFilter<T>(AccessContext subject) where T : IClassified
    {
        ArgumentNullException.ThrowIfNull(subject);

        if (typeof(T) != typeof(Document))
        {
            return _ => true;
        }

        var role = ResolveRole(subject);

        // Исполнитель — «поручения, где он ответственный» (§2.1): в терминах документа это документы,
        // где у него есть ХОТЯ БЫ одно назначение (Any по навигации — EF транслирует в EXISTS).
        Expression<Func<Document, bool>> documentFilter = role switch
        {
            UserRole.Administrator => _ => false,
            UserRole.Manager => _ => true,
            UserRole.Inspector => d => d.InspectorUserId == subject.NumericSubjectId,
            UserRole.Performer => d => d.Assignments.Any(a => a.AssigneeUserId == subject.NumericSubjectId),
            _ => _ => false,
        };

        return (Expression<Func<T, bool>>)(object)documentFilter;
    }

    // Синхронный запрос НАРОЧНО (не async): контракт IAccessPolicy.BuildFilter синхронный (ядро, менять
    // не наш повод) — обычный блокирующий ADO-вызов по первичному ключу на маленькой таблице, не
    // async-over-sync антипаттерн (нет захваченного Task/deadlock-риска классического SynchronizationContext).
    private UserRole? ResolveRole(AccessContext subject)
    {
        if (subject.NumericSubjectId is not { } userId)
        {
            return null;
        }

        using var db = contextFactory.CreateDbContext();
        return db.UserRoleAssignments.AsNoTracking()
            .Where(r => r.UserId == userId)
            .Select(r => (UserRole?)r.Role)
            .FirstOrDefault();
    }
}
