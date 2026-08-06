using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Inspector.Domain.Enums;
using ISC.AI.Profile.Inspector.Domain.Services;
using Mediator;

namespace ISC.AI.Profile.Inspector.Application.Roles;

/// <summary>
/// Сценарии ведения ролей пользователей (§2.1 ТЗ СКИД, этап 6 Э4-35). Проверка «вызывающий —
/// Администратор» — ЗДЕСЬ, в обработчике, а не только на странице UI (тот же урок, что аудит слияния
/// вынес для write-сценариев докфлоу без проверки допуска, Э4-35 §6.2): без серверной проверки первый
/// же новый вызывающий (другая страница, будущий API) обошёл бы ограничение.
/// </summary>

/// <summary>Все пользователи с их текущей ролью (для страницы администрирования).</summary>
public sealed record ListUserRolesQuery : IRequest<ResponseDto<IReadOnlyList<UserRoleRow>>>
{
    /// <inheritdoc cref="ListUserRolesQuery" />
    public sealed class Handler(IUserRoleStore store, ISubjectProvider subjectProvider)
        : IRequestHandler<ListUserRolesQuery, ResponseDto<IReadOnlyList<UserRoleRow>>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<IReadOnlyList<UserRoleRow>>> Handle(
            ListUserRolesQuery query, CancellationToken cancellationToken)
        {
            if (!await RoleScenariosGuard.CallerCanManageRolesAsync(store, subjectProvider, cancellationToken))
            {
                return ResponseDto<IReadOnlyList<UserRoleRow>>.BadRequest(
                    "Список и назначение ролей доступны только Администратору.");
            }

            var items = await store.ListAsync(cancellationToken);
            return ResponseDto<IReadOnlyList<UserRoleRow>>.Ok(items, items.Count);
        }
    }
}

/// <summary>Назначить роль пользователю (<paramref name="Role"/> = <see langword="null"/> — снять роль).</summary>
public sealed record SetUserRoleCommand(int UserId, UserRole? Role) : IRequest<ResponseDto<bool>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.Modify;

    /// <inheritdoc />
    public string? AuditSummary => $"inspector:user-role:{UserId}:{(Role is { } r ? r.ToString() : "снята")}";

    /// <inheritdoc cref="SetUserRoleCommand" />
    public sealed class Handler(IUserRoleStore store, ISubjectProvider subjectProvider)
        : IRequestHandler<SetUserRoleCommand, ResponseDto<bool>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<bool>> Handle(SetUserRoleCommand command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);

            if (!await RoleScenariosGuard.CallerCanManageRolesAsync(store, subjectProvider, cancellationToken))
            {
                return ResponseDto<bool>.BadRequest("Назначение ролей доступно только Администратору.");
            }

            await store.SetRoleAsync(command.UserId, command.Role, cancellationToken);
            return ResponseDto<bool>.Ok(true);
        }
    }
}

/// <summary>Общая проверка вызывающего для обоих сценариев (см. remarks класса).</summary>
/// <remarks>
/// Опирается на <see cref="ISubjectProvider"/> («кто вошёл»), а НЕ на <c>IAccessContextProvider</c>
/// («что вошедшему можно»). Это исправление отдельного отказа, зафиксированного проверкой 6.4.2:
/// на ЧИСТОЙ установке ни у кого нет записи в <c>core.clearance</c>, поэтому <c>GetCurrentAsync</c>
/// бросал <c>AccessContextRequiredException</c> — и страница ролей падала ИМЕННО ТАМ, где режим
/// первичной настройки и нужен. Право распоряжаться ролями определяется ролью, а не допуском.
/// </remarks>
file static class RoleScenariosGuard
{
    /// <summary>
    /// Вправе ли вызывающий видеть и назначать роли. Обычное правило — только Администратор; но пока
    /// В СИСТЕМЕ НЕТ НИ ОДНОГО АДМИНИСТРАТОРА, действует РЕЖИМ ПЕРВИЧНОЙ НАСТРОЙКИ: иначе назначить
    /// первого Администратора некому (страница требует Администратора — замок без ключа; ровно так
    /// система и оказалась запертой сразу после выпуска этапа 6.4).
    /// </summary>
    /// <remarks>
    /// Условие — «нет Администратора», НЕ «реестр ролей пуст». Проверка пустоты (первая редакция
    /// фикса) закрывала окно ЛЮБОЙ первой ролью: назначил себе «Руководителя» (единственная роль,
    /// которая видит все документы) — Администратора нет, окно закрыто, управление ролями потеряно
    /// навсегда. Текущее правило самовосстанавливающееся: не стало Администратора — окно открылось.
    /// </remarks>
    public static async Task<bool> CallerCanManageRolesAsync(
        IUserRoleStore store, ISubjectProvider subjectProvider, CancellationToken cancellationToken)
    {
        if (await subjectProvider.GetCurrentUserIdAsync(cancellationToken) is not { } callerId)
        {
            return false;
        }

        if (await store.GetRoleAsync(callerId, cancellationToken) == UserRole.Administrator)
        {
            return true;
        }

        return !await store.AnyAdministratorAsync(cancellationToken);
    }
}
