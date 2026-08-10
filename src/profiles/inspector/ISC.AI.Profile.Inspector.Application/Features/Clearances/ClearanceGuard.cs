using FluentValidation;
using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Inspector.Domain.Enums;
using ISC.AI.Profile.Inspector.Domain.Services;
using Mediator;

namespace ISC.AI.Profile.Inspector.Application.Features.Clearances;

/// <summary>Кто вправе распоряжаться допусками — то же правило, что у ролей (§2.1).</summary>
/// <remarks>
/// Правило намеренно повторяет <c>RoleScenariosGuard</c>: Администратор, а пока Администратора в
/// системе НЕТ — любой вошедший (режим первичной настройки, Э4-35 §6.4.1). Иначе на чистом контуре
/// выдать первый допуск было бы некому — тот же замок без ключа, что уже случался с ролями.
/// Опора — <see cref="ISubjectProvider"/>, а не контекст допуска: у распорядителя допуска может ещё
/// не быть, и это нормально.
/// </remarks>
public static class ClearanceGuard
{
    /// <summary>Единый текст отказа — чтобы он не разошёлся между четырьмя сценариями (и был проверяем тестом).</summary>
    public const string Denied = "Просмотр и выдача допусков доступны только Администратору.";

    /// <inheritdoc cref="ClearanceGuard" />
    public static async Task<bool> CallerCanManageAsync(
        IUserRoleStore roles, ISubjectProvider subjectProvider, CancellationToken cancellationToken)
    {
        if (await subjectProvider.GetCurrentUserIdAsync(cancellationToken) is not { } callerId)
        {
            return false;
        }

        if (await roles.GetRoleAsync(callerId, cancellationToken) == UserRole.Administrator)
        {
            return true;
        }

        return !await roles.AnyAdministratorAsync(cancellationToken);
    }
}
