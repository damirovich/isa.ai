using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.Admin.Domain.Services;
using ISC.AI.Profile.Investigation.Domain.Enums;
using ISC.AI.Profile.Investigation.Domain.Services;
using Microsoft.EntityFrameworkCore;

namespace ISC.AI.Profile.Investigation.Data;

/// <summary>
/// Реализация порта <see cref="IPlatformAdministration"/> — кто в профиле «Следствие» вправе вести
/// учётные записи и допуски и кто вправе читать журнал аудита (ADR-0023). Право определяется РОЛЬЮ
/// (ТП-004), роли ведёт профиль, пакет администрирования о профиле не знает.
/// </summary>
public sealed class InvestigationPlatformAdministration(IUserRoleStore roles, ISubjectProvider subjectProvider)
    : IPlatformAdministration
{
    /// <inheritdoc />
    /// <remarks>
    /// ИНВАРИАНТ (ТБ-012): правило ОДНО с ведением ролей, справочников и настроек документооборота
    /// (<see cref="AdministrationRule"/>): Администратор — всегда; любой вошедший — только пока
    /// Администратора нет ни одного (режим первичной настройки, иначе на чистом контуре «замок без
    /// ключа»: роль назначить некому, потому что назначение роли само требует роли).
    /// </remarks>
    public Task<bool> CanManageAsync(CancellationToken cancellationToken = default) =>
        AdministrationRule.CallerCanManageAsync(roles, subjectProvider, cancellationToken);

    /// <inheritdoc />
    /// <remarks>
    /// ИНВАРИАНТ (ТБ-030/032, ТП-004): журнал читает Администратор И Офицер ИБ — роль, которая
    /// учётными записями и допусками НЕ распоряжается. Поэтому право на журнал шире права на ведение
    /// и спрашивается отдельно. Режим первичной настройки унаследован от <see cref="CanManageAsync"/>:
    /// пока Администратора нет, журнал доступен любому вошедшему — иначе на чистом контуре нельзя было
    /// бы проверить даже собственные действия по настройке. Что именно субъект увидит в журнале,
    /// решает решётка гриф/подразделение в <c>IAuditReader</c> (ТБ-032): право даёт ОТКРЫТЬ журнал,
    /// а не видеть в нём всё.
    /// </remarks>
    public async Task<bool> CanViewAuditAsync(CancellationToken cancellationToken = default)
    {
        if (await AdministrationRule.CallerHasRoleAsync(
            roles, subjectProvider, cancellationToken, InvestigationRole.SecurityOfficer))
        {
            return true;
        }

        return await CanManageAsync(cancellationToken);
    }
}

/// <summary>
/// Реализация порта <see cref="IDivisionCatalog"/> — наименования подразделений профиля
/// (<c>investigation.division</c>) для экрана допусков пакета администрирования.
/// </summary>
/// <remarks>
/// Отдаются ВСЕ подразделения, включая НЕДЕЙСТВУЮЩИЕ (этим порт отличается от справочника
/// документооборота, где идёт выбор исполнителя): допуск живёт дольше подразделения, и в
/// <c>core.clearance.division_scope</c> остаются номера закрытых — экран обязан объяснить их
/// наименованием, а не показать «неизвестный номер».
/// </remarks>
public sealed class InvestigationDivisionCatalog(IDbContextFactory<InvestigationDbContext> contextFactory)
    : IDivisionCatalog
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<DivisionCatalogItem>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        var all = await db.Divisions.AsNoTracking()
            .Select(d => new { d.Id, d.Name, d.ParentId, d.IsActive })
            .ToListAsync(cancellationToken);

        // Иерархия разворачивается плоско «Родитель / Дочернее»: в допуске лежит номер, и различить
        // одноимённые отделы разных управлений иначе нечем.
        var names = all.ToDictionary(d => d.Id, d => d.Name);

        return all
            .Select(d => new DivisionCatalogItem(
                d.Id,
                d.ParentId is { } parentId && names.TryGetValue(parentId, out var parent)
                    ? parent + " / " + d.Name
                    : d.Name,
                d.IsActive))
            .OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }
}

/// <summary>
/// Реализация порта <see cref="IUserRoleCatalog"/> — роли профиля (ТП-004) и их назначения для
/// колонки и фильтра на экране учётных записей. Пакет роли только ПОКАЗЫВАЕТ: назначает их страница
/// профиля «Роли пользователей» (<c>/admin/roles</c>).
/// </summary>
/// <remarks>
/// Ключ роли — <c>InvestigationRole.ToString()</c>: пакету он непрозрачен, а профилю разбирать его
/// обратно не нужно — назначение идёт своим сценарием со своим типом.
/// </remarks>
public sealed class InvestigationUserRoleCatalog(IUserRoleStore roles) : IUserRoleCatalog
{
    /// <inheritdoc />
    public Task<IReadOnlyList<RoleOption>> ListRolesAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<RoleOption> options =
        [
            .. Enum.GetValues<InvestigationRole>()
                .Select(role => new RoleOption(role.ToString(), role.Label())),
        ];

        return Task.FromResult(options);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<int, string>> GetUserRoleKeysAsync(CancellationToken cancellationToken = default)
    {
        var rows = await roles.ListAsync(cancellationToken);

        // Пользователи без роли в словарь не входят — экран покажет «не назначена».
        return rows
            .Where(row => row.Role is not null)
            .ToDictionary(row => row.UserId, row => row.Role!.Value.ToString());
    }
}
