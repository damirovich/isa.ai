using ISC.AI.Modules.DocFlow.Domain.Services;
using Microsoft.EntityFrameworkCore;

namespace ISC.AI.Profile.Inspector.Data;

/// <summary>
/// Справочник подразделений для модуля документооборота поверх иерархии профиля
/// (<c>inspector.division</c>, ТУ→РО). Реализация порта <see cref="IDivisionDirectory"/> — вопрос 3
/// Э4-35: словарь идентификаторов един с решёткой доступа ядра, справочник ведёт профиль, модуль
/// получает его через DI (инверсия как у <c>IAccessPolicy</c>, ADR-0014).
/// </summary>
public sealed class DocFlowDivisionDirectory(IDbContextFactory<InspectorDbContext> contextFactory)
    : IDivisionDirectory
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<DivisionItem>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        // Иерархию показываем плоско «родитель / дочернее» — модулю достаточно имени и id.
        return await db.Divisions.AsNoTracking()
            .OrderBy(d => d.Parent != null ? d.Parent.Name : d.Name)
            .ThenBy(d => d.Name)
            .Select(d => new DivisionItem(
                d.Id,
                d.Parent != null ? d.Parent.Name + " / " + d.Name : d.Name))
            .ToListAsync(cancellationToken);
    }
}
