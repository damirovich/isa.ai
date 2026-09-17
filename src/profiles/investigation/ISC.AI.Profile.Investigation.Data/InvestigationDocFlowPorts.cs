using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.DocFlow.Domain.Services;
using ISC.AI.Persistence;
using ISC.AI.Profile.Investigation.Domain.Enums;
using ISC.AI.Profile.Investigation.Domain.Services;
using Microsoft.EntityFrameworkCore;

namespace ISC.AI.Profile.Investigation.Data;

/// <summary>
/// Справочник подразделений для модуля документооборота поверх иерархии профиля
/// (<c>investigation.division</c>). Реализация порта <see cref="IDivisionDirectory"/>: словарь
/// идентификаторов един с решёткой доступа ядра, справочник ведёт профиль, модуль получает его через DI
/// (инверсия как у <c>IAccessPolicy</c>, ADR-0014/0017).
/// </summary>
public sealed class InvestigationDivisionDirectory(IDbContextFactory<InvestigationDbContext> contextFactory)
    : IDivisionDirectory
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<DivisionItem>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        // Иерархия плоско «Родитель / Дочернее»; имена родителей берутся из полного справочника (родитель
        // может быть выведен из обращения — подпись у действующего дочернего всё равно нужна). Только
        // действующие: это список для ВЫБОРА, расформированному подразделению документ не адресуют.
        var all = await db.Divisions.AsNoTracking()
            .Select(d => new { d.Id, d.Name, d.ParentId, d.IsActive })
            .ToListAsync(cancellationToken);

        var names = all.ToDictionary(d => d.Id, d => d.Name);

        return all
            .Where(d => d.IsActive)
            .Select(d => new DivisionItem(
                d.Id,
                d.ParentId is { } parentId && names.TryGetValue(parentId, out var parent)
                    ? parent + " / " + d.Name
                    : d.Name))
            .OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }
}

/// <summary>
/// Реализация порта <see cref="IDocFlowAdministration"/> — кто вправе вести настройки модуля
/// документооборота. Право определяется РОЛЬЮ, роли ведёт профиль, модуль на профиль не ссылается (ADR-0017).
/// </summary>
public sealed class InvestigationDocFlowAdministration(IUserRoleStore roles, ISubjectProvider subjectProvider)
    : IDocFlowAdministration
{
    /// <inheritdoc />
    /// <remarks>
    /// Правило ОДНО с ведением учётных записей (<see cref="AdministrationRule"/>): Администратор — всегда;
    /// любой вошедший — только пока Администратора нет (иначе на чистом контуре «замок без ключа»).
    /// </remarks>
    public Task<bool> CanManageAsync(CancellationToken cancellationToken = default) =>
        AdministrationRule.CallerCanManageAsync(roles, subjectProvider, cancellationToken);
}

/// <summary>
/// Кого профиль предлагает модулю документооборота в «ответственные» и «исполнители» (реализация
/// <see cref="IAssignmentCandidateDirectory"/>). В терминах профиля «инспектор» документа — Следователь.
/// </summary>
/// <remarks>
/// Роли живут в схеме профиля, допуски — в ядре, поэтому список собирается из двух источников и
/// пересекается в памяти: обе таблицы размером со штат организации.
/// </remarks>
public sealed class InvestigationAssignmentCandidateDirectory(
    IDbContextFactory<CoreDbContext> coreContextFactory,
    IUserRoleStore roles) : IAssignmentCandidateDirectory
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<UserItem>> ListInspectorsAsync(CancellationToken cancellationToken = default)
    {
        var active = await ActiveUsersAsync(cancellationToken);
        var investigators = (await roles.ListUserIdsByRoleAsync(InvestigationRole.Investigator, cancellationToken)).ToHashSet();

        // ЗАПАСНОЙ ВАРИАНТ: пока роль «Следователь» никому не назначена, сузить список не по чему —
        // пустой список запер бы регистрацию документов на свежем контуре («замок без ключа»).
        return investigators.Count == 0
            ? active
            : [.. active.Where(user => investigators.Contains(user.Id))];
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<UserItem>> ListAssigneesAsync(int divisionId, CancellationToken cancellationToken = default)
    {
        await using var core = await coreContextFactory.CreateDbContextAsync(cancellationToken);

        // Отбор по ДОПУСКУ подразделения — тот же критерий, что у серверной проверки (ТБ-020/021):
        // исполнитель без допуска к подразделению документа его просто не увидит.
        var candidates = await core.Users.AsNoTracking()
            .Where(u => u.IsActive
                && u.Clearance != null
                && u.Clearance.DivisionScope.Contains(divisionId))
            .OrderBy(u => u.DisplayName ?? u.UserName)
            .Select(u => new UserItem(u.Id, u.DisplayName ?? u.UserName))
            .ToListAsync(cancellationToken);

        // Исполняют поручения Следователь, Руководитель и Эксперт по лицам; Администратор, Верификатор
        // и Офицер ИБ — нет. Но только если роли вообще назначены, иначе список опустеет.
        var rows = await roles.ListAsync(cancellationToken);
        var executors = rows
            .Where(r => r.Role is InvestigationRole.Investigator or InvestigationRole.Head or InvestigationRole.FaceExpert)
            .Select(r => r.UserId)
            .ToHashSet();

        return executors.Count == 0
            ? candidates
            : [.. candidates.Where(user => executors.Contains(user.Id))];
    }

    private async Task<IReadOnlyList<UserItem>> ActiveUsersAsync(CancellationToken cancellationToken)
    {
        await using var core = await coreContextFactory.CreateDbContextAsync(cancellationToken);

        return await core.Users.AsNoTracking()
            .Where(u => u.IsActive)
            .OrderBy(u => u.DisplayName ?? u.UserName)
            .Select(u => new UserItem(u.Id, u.DisplayName ?? u.UserName))
            .ToListAsync(cancellationToken);
    }
}
