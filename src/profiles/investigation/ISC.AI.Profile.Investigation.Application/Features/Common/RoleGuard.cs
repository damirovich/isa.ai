using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Investigation.Domain.Services;

namespace ISC.AI.Profile.Investigation.Application.Features.Common;

/// <summary>
/// Единая охрана сценариев профиля «Следствие» по матрице доступа (ТП-004, ADR-0032). Само правило — в домене
/// (<see cref="PermissionRule"/>, <see cref="InvestigationPermissions"/>): здесь только именованные обёртки и тексты
/// отказов, чтобы сценарии не расходились друг с другом, с портами пакетов в слое данных и с меню.
/// </summary>
/// <remarks>
/// Опора — <see cref="ISubjectProvider"/> («кто вошёл»), а НЕ контекст допуска: у распорядителя ролей и
/// допусков на чистом контуре допуска ещё нет (замок без ключа, Э4-35 §6.4.1). Выдача ДАННЫХ дел
/// по-прежнему идёт через <see cref="IAccessContextProvider"/> и решётку гриф/подразделение
/// (ТБ-020/021) — гвард прав её не заменяет, а дополняет.
/// </remarks>
public static class RoleGuard
{
    /// <summary>Отказ ведения пользователей и ролей — право закреплено за Администратором.</summary>
    public const string AdminDenied = AdministrationRule.Denied;

    /// <summary>Отказ операций с делами и фигурантами.</summary>
    public static readonly string CaseDenied = Denied(InvestigationPermissions.CasesEdit);

    /// <summary>Отказ ведения подразделений и справочников.</summary>
    public static readonly string DirectoriesDenied = Denied(InvestigationPermissions.AdminDirectories);

    /// <summary>Отказ для сценариев, требующих хотя бы какой-то роли профиля.</summary>
    public const string NoRoleDenied = "У вас нет роли в профиле «Следствие»: обратитесь к Администратору.";

    /// <summary>Текст отказа по праву: какое действие закрыто и к кому идти.</summary>
    public static string Denied(string permission) =>
        $"Действие «{InvestigationPermissions.Get(permission).Label}» закрыто для вашей роли в матрице доступа. Обратитесь к Администратору.";

    /// <summary>Есть ли у субъекта право матрицы доступа (<see cref="PermissionRule.CallerHasAsync"/>).</summary>
    public static Task<bool> CallerHasAsync(
        IUserRoleStore roles, ISubjectProvider subjectProvider, string permission, CancellationToken cancellationToken) =>
        PermissionRule.CallerHasAsync(roles, subjectProvider, permission, cancellationToken);

    /// <summary>
    /// Вправе ли субъект вести пользователей и роли: Администратор, а пока Администратора нет ни одного —
    /// любой вошедший (режим первичной настройки). Без аутентификации — всегда отказ.
    /// </summary>
    public static Task<bool> CallerCanManageAsync(
        IUserRoleStore roles, ISubjectProvider subjectProvider, CancellationToken cancellationToken) =>
        AdministrationRule.CallerCanManageAsync(roles, subjectProvider, cancellationToken);

    /// <summary>Вправе ли субъект вести подразделения и справочники (с режимом первичной настройки).</summary>
    public static Task<bool> CallerCanManageDirectoriesAsync(
        IUserRoleStore roles, ISubjectProvider subjectProvider, CancellationToken cancellationToken) =>
        CallerHasAsync(roles, subjectProvider, InvestigationPermissions.AdminDirectories, cancellationToken);

    /// <summary>
    /// Вправе ли субъект вести дела. Режима первичной настройки здесь НЕТ: операции с делами без роли невозможны,
    /// даже если Администратор ещё не назначен.
    /// </summary>
    public static Task<bool> CallerCanEditCasesAsync(
        IUserRoleStore roles, ISubjectProvider subjectProvider, CancellationToken cancellationToken) =>
        CallerHasAsync(roles, subjectProvider, InvestigationPermissions.CasesEdit, cancellationToken);

    /// <summary>Есть ли у субъекта хоть какая-то роль профиля (справочные выборки для форм).</summary>
    public static async Task<bool> CallerHasAnyRoleAsync(
        IUserRoleStore roles, ISubjectProvider subjectProvider, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(roles);
        ArgumentNullException.ThrowIfNull(subjectProvider);

        if (await subjectProvider.GetCurrentUserIdAsync(cancellationToken) is not { } userId)
        {
            return false;
        }

        return await roles.GetRoleAsync(userId, cancellationToken) is not null;
    }
}
