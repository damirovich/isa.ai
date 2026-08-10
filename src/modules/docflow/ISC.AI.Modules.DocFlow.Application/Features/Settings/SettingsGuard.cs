using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.DocFlow.Domain.Services;
using Mediator;

namespace ISC.AI.Modules.DocFlow.Application.Features.Settings;

/// <summary>Единый текст отказа для настроек модуля.</summary>
public static class SettingsGuard
{
    /// <inheritdoc cref="SettingsGuard" />
    public const string Denied = "Изменение системных настроек доступно только Администратору.";
}
