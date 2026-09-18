using System.Linq.Expressions;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.DocFlow.Domain.Entities;
using ISC.AI.Profile.Investigation.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ISC.AI.Profile.Investigation.Data;

/// <summary>
/// Построчные правила доступа профиля «Следствие» (ТП-004) — СУЖАЮЩАЯ политика поверх floor'а ядра
/// (ADR-0014): floor уже отсеял гриф/подразделение (ТБ-020/021), эта политика дополнительно сужает по
/// РОЛИ и ВЛАДЕНИЮ. Переопределяет <c>AllowAllAccessPolicy</c> ядра повторной регистрацией
/// <see cref="IAccessPolicy"/> в контейнере (см. <c>AddInvestigationPersistence</c>).
/// </summary>
/// <remarks>
/// Правило действует ТОЛЬКО для документов документооборота (<see cref="Document"/>): Администратор и
/// Руководитель — всё; Следователь — документы, где он ответственный или регистратор; прочие роли —
/// только зарегистрированные ими; без роли — ничего (default-deny, ТБ-012).
///
/// Для любого другого <c>T</c> (дела, фигуранты, шаблоны лиц, эмбеддинги корпуса) политика возвращает
/// разрешающее <c>_ =&gt; true</c> — и это НЕ дыра: дела и фигуранты сужаются по роли/владению в
/// <see cref="CaseAccessRule"/> внутри хранилищ, а область поиска по лицам — через <c>ICaseScope</c>
/// (дела субъекта → носители дел → шаблоны). Делать это здесь нельзя: контракт <see cref="BuildFilter{T}"/>
/// синхронный и одноконтекстный — предикат по делу для шаблона лица потребовал бы подзапроса в ЧУЖУЮ
/// схему (<c>media</c> → <c>investigation</c>) из другого <c>DbContext</c>, что EF не транслирует, а
/// роль читается синхронно. Floor ядра при этом применяется всегда и не отключается.
///
/// Приведение результата к <c>Expression&lt;Func&lt;T, bool&gt;&gt;</c> безопасно ТОЛЬКО потому, что оно
/// происходит в ветке, проверившей <c>typeof(T) == typeof(Document)</c>.
/// </remarks>
public sealed class InvestigationAccessPolicy(IDbContextFactory<InvestigationDbContext> contextFactory) : IAccessPolicy
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
        var me = subject.NumericSubjectId;

        Expression<Func<Document, bool>> documentFilter = role switch
        {
            InvestigationRole.Administrator => _ => true,
            InvestigationRole.Head => _ => true,
            InvestigationRole.Investigator => d => d.InspectorUserId == me || d.RegisteredByUserId == me,
            InvestigationRole.FaceExpert
                or InvestigationRole.Verifier
                or InvestigationRole.SecurityOfficer => d => d.RegisteredByUserId == me,
            _ => _ => false,
        };

        return (Expression<Func<T, bool>>)(object)documentFilter;
    }

    // Синхронный запрос НАРОЧНО (не async): контракт IAccessPolicy.BuildFilter синхронный (ядро) —
    // блокирующий ADO-вызов по уникальному индексу на маленькой таблице, не async-over-sync.
    private InvestigationRole? ResolveRole(AccessContext subject)
    {
        if (subject.NumericSubjectId is not { } userId)
        {
            return null;
        }

        using var db = contextFactory.CreateDbContext();
        return db.UserRoleAssignments.AsNoTracking()
            .Where(r => r.UserId == userId)
            .Select(r => (InvestigationRole?)r.Role)
            .FirstOrDefault();
    }
}
