using ISC.AI.Abstractions.Security;

namespace ISC.AI.Modules.Admin.Application.Features.Accounts;

/// <summary>Учётная запись с ролью — строка расширенного списка администрирования.</summary>
/// <param name="Account">Строка реестра учётных записей ядра (<c>core.app_user</c>).</param>
/// <param name="RoleKey">Ключ назначенной роли; <see langword="null"/> — роли нет.</param>
/// <param name="RoleLabel">
/// Подпись роли на языке эксплуатанта; <see langword="null"/> — роли нет либо профиль перестал
/// объявлять такой ключ (тогда экран покажет ключ — молчать о назначенном праве нельзя).
/// </param>
public sealed record UserAccountView(UserAccountRow Account, string? RoleKey, string? RoleLabel);
