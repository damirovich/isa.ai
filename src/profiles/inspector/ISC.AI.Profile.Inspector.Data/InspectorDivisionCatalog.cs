using ISC.AI.Modules.Admin.Domain.Services;
using Microsoft.EntityFrameworkCore;

namespace ISC.AI.Profile.Inspector.Data;

/// <summary>
/// Реализация порта <see cref="IDivisionCatalog"/> — наименования подразделений профиля
/// (<c>inspector.division</c>, ТУ→РО) для экрана допусков пакета администрирования.
/// </summary>
/// <remarks>
/// Отдаются ВСЕ подразделения, включая выведенные из обращения, — этим справочник отличается от
/// <see cref="DocFlowDivisionDirectory"/>, который отдаёт только действующие. Причина в назначении:
/// там список для ВЫБОРА исполнителя, а здесь объяснение того, что уже выдано. В допуске
/// (<c>core.clearance.division_scope</c>) остаются номера закрытых подразделений, и экран обязан
/// показать их наименованием, иначе расхождение выглядит как «неизвестный номер» и не разбирается.
/// </remarks>
public sealed class InspectorDivisionCatalog(IDbContextFactory<InspectorDbContext> contextFactory)
    : IDivisionCatalog
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<DivisionCatalogItem>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        // Иерархию показываем плоско «родитель / дочернее»: экрану допусков достаточно имени,
        // а по одному лишь названию дочернего подразделения его не опознать.
        return await db.Divisions.AsNoTracking()
            .OrderBy(d => d.Parent != null ? d.Parent.Name : d.Name)
            .ThenBy(d => d.Name)
            .Select(d => new DivisionCatalogItem(
                d.Id,
                d.Parent != null ? d.Parent.Name + " / " + d.Name : d.Name,
                d.IsActive))
            .ToListAsync(cancellationToken);
    }
}
