using ISC.AI.Modules.DocFlow.Domain.Services;
using ISC.AI.Persistence;
using ISC.AI.Profile.Inspector.Domain.Enums;
using ISC.AI.Profile.Inspector.Domain.Services;
using Microsoft.EntityFrameworkCore;

namespace ISC.AI.Profile.Inspector.Data;

/// <summary>
/// Кого профиль предлагает модулю в инспекторы и исполнители (реализация
/// <see cref="IAssignmentCandidateDirectory"/>).
/// </summary>
/// <remarks>
/// Роли живут в схеме профиля, допуски — в ядре, поэтому список собирается из двух источников
/// и пересекается в памяти: обе таблицы размером со штат организации.
/// </remarks>
public sealed class AssignmentCandidateDirectory(
    IDbContextFactory<CoreDbContext> coreContextFactory,
    IUserRoleStore roles) : IAssignmentCandidateDirectory
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<UserItem>> ListInspectorsAsync(
        CancellationToken cancellationToken = default)
    {
        var active = await ActiveUsersAsync(cancellationToken);
        var byRole = await UsersInRoleAsync(UserRole.Inspector, cancellationToken);

        // ЗАПАСНОЙ ВАРИАНТ: пока роль «Инспектор» не назначена никому, сузить список не по чему —
        // и отдать пустой означало бы, что документ группы «Исполнение» зарегистрировать НЕЛЬЗЯ
        // (инспектор для неё обязателен). Тот же класс отказа, что «замок без ключа» с ролями
        // (§6.4.1): свежее развёртывание не должно запирать систему. Как только первый Инспектор
        // назначен, список сужается сам.
        return byRole.Count == 0
            ? active
            : [.. active.Where(user => byRole.Contains(user.Id))];
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<UserItem>> ListAssigneesAsync(
        int divisionId, CancellationToken cancellationToken = default)
    {
        await using var core = await coreContextFactory.CreateDbContextAsync(cancellationToken);

        // Отбор по ДОПУСКУ, а не по «подразделению пользователя»: такого поля у нас нет вовсе,
        // связь человека с подразделениями выражена допуском (см. IUserDirectory.CanSeeDivisionAsync).
        // Здесь тот же критерий, что у серверной проверки, — иначе форма предлагала бы людей,
        // которых сервер потом отклонит.
        var candidates = await core.Users.AsNoTracking()
            .Where(u => u.IsActive
                && u.Clearance != null
                && u.Clearance.DivisionScope.Contains(divisionId))
            .OrderBy(u => u.DisplayName ?? u.UserName)
            .Select(u => new UserItem(u.Id, u.DisplayName ?? u.UserName))
            .ToListAsync(cancellationToken);

        // Руководители и администраторы поручений не исполняют — их из списка убираем. Но только
        // если роли вообще назначены: иначе (свежая система) список опустеет и назначить будет некого.
        var executors = await UsersInExecutingRolesAsync(cancellationToken);
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

    private async Task<HashSet<int>> UsersInRoleAsync(UserRole role, CancellationToken cancellationToken)
    {
        var rows = await roles.ListAsync(cancellationToken);
        return [.. rows.Where(r => r.Role == role).Select(r => r.UserId)];
    }

    /// <summary>Роли, которые ИСПОЛНЯЮТ поручения: исполнитель и инспектор.</summary>
    /// <remarks>
    /// Инспектор включён намеренно: он ведёт документ и нередко исполняет поручение сам, а запрет
    /// заставил бы заводить ему вторую учётку. Руководитель и Администратор исключены — они
    /// распределяют и настраивают, а не исполняют.
    /// </remarks>
    private async Task<HashSet<int>> UsersInExecutingRolesAsync(CancellationToken cancellationToken)
    {
        var rows = await roles.ListAsync(cancellationToken);
        return
        [
            .. rows
                .Where(r => r.Role is UserRole.Performer or UserRole.Inspector)
                .Select(r => r.UserId),
        ];
    }
}
