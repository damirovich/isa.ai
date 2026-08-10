using System.Security.Cryptography;
using FluentValidation;
using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Inspector.Domain.Enums;
using ISC.AI.Profile.Inspector.Domain.Services;
using Mediator;

namespace ISC.AI.Profile.Inspector.Application.Features.Accounts;

/// <summary>Кто вправе вести учётные записи — то же правило, что у ролей и допусков (§2.1).</summary>
public static class AccountGuard
{
    /// <summary>Единый текст отказа.</summary>
    public const string Denied = "Ведение учётных записей доступно только Администратору.";

    /// <inheritdoc cref="AccountGuard" />
    /// <remarks>
    /// Само правило — в домене (<see cref="AdministrationRule"/>): его же спрашивает слой данных,
    /// отдавая модулю документооборота реализацию его порта администрирования, а разъехавшиеся копии
    /// правила доступа замечает не разработчик, а посторонний.
    /// </remarks>
    public static Task<bool> CallerCanManageAsync(
        IUserRoleStore roles, ISubjectProvider subjectProvider, CancellationToken cancellationToken) =>
        AdministrationRule.CallerCanManageAsync(roles, subjectProvider, cancellationToken);
}
