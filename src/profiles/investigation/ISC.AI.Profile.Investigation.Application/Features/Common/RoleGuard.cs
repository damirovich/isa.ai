using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Investigation.Domain.Enums;
using ISC.AI.Profile.Investigation.Domain.Services;

namespace ISC.AI.Profile.Investigation.Application.Features.Common;

/// <summary>
/// Единая охрана сценариев профиля «Следствие» по ролям (ТП-004). Само правило — в домене
/// (<see cref="AdministrationRule"/>): здесь только именованные обёртки и тексты отказов, чтобы
/// сценарии не расходились друг с другом и с реализацией портов модуля «Медиа» в слое данных.
/// </summary>
/// <remarks>
/// Опора — <see cref="ISubjectProvider"/> («кто вошёл»), а НЕ контекст допуска: у распорядителя ролей и
/// допусков на чистом контуре допуска ещё нет (замок без ключа, Э4-35 §6.4.1). Выдача ДАННЫХ дел
/// по-прежнему идёт через <see cref="IAccessContextProvider"/> и решётку гриф/подразделение
/// (ТБ-020/021) — гвард ролей её не заменяет, а дополняет.
/// </remarks>
public static class RoleGuard
{
    /// <summary>Отказ администрирования (роли, допуски, справочники).</summary>
    public const string AdminDenied = AdministrationRule.Denied;

    /// <summary>Отказ операций с делами и фигурантами.</summary>
    public const string CaseDenied = "Ведение дел доступно Следователю, Руководителю и Администратору.";

    /// <summary>Отказ для сценариев, требующих хотя бы какой-то роли профиля.</summary>
    public const string NoRoleDenied = "У вас нет роли в профиле «Следствие»: обратитесь к Администратору.";

    /// <summary>Роли, ведущие дела: заводят дела, фигурантов, основания поиска (ТФ-ДЕЛ-01, ТФ-ПЕР-01).</summary>
    public static readonly IReadOnlyList<InvestigationRole> CaseEditors =
        [InvestigationRole.Investigator, InvestigationRole.Head, InvestigationRole.Administrator];

    /// <summary>
    /// Вправе ли субъект администрировать: Администратор, а пока Администратора нет ни одного —
    /// любой вошедший (режим первичной настройки). Без аутентификации — всегда отказ.
    /// </summary>
    public static Task<bool> CallerCanManageAsync(
        IUserRoleStore roles, ISubjectProvider subjectProvider, CancellationToken cancellationToken) =>
        AdministrationRule.CallerCanManageAsync(roles, subjectProvider, cancellationToken);

    /// <summary>
    /// Есть ли у субъекта одна из перечисленных ролей. Режима первичной настройки здесь НЕТ намеренно:
    /// операции с делами без роли невозможны, даже если Администратор ещё не назначен.
    /// </summary>
    public static Task<bool> CallerHasRoleAsync(
        IUserRoleStore roles,
        ISubjectProvider subjectProvider,
        IReadOnlyList<InvestigationRole> allowed,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(allowed);
        return AdministrationRule.CallerHasRoleAsync(roles, subjectProvider, cancellationToken, [.. allowed]);
    }

    /// <summary>Вправе ли субъект вести дела (<see cref="CaseEditors"/>).</summary>
    public static Task<bool> CallerCanEditCasesAsync(
        IUserRoleStore roles, ISubjectProvider subjectProvider, CancellationToken cancellationToken) =>
        CallerHasRoleAsync(roles, subjectProvider, CaseEditors, cancellationToken);

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
