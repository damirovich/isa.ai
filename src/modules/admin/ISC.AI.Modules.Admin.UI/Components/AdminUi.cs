using System.Globalization;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.Admin.Application.Features.Accounts;
using MudBlazor;

namespace ISC.AI.Modules.Admin.UI;

/// <summary>
/// Общие подписи и цвета экрана «Пользователи»: состояние учётной записи, допуск, инициалы. Одно место — список и
/// карточка сотрудника показывают одно и то же одинаково.
/// </summary>
public static class AdminUi
{
    /// <summary>Состояние учётной записи: подпись и цвет чипа.</summary>
    public static (string Label, Color Color) Status(UserAccountRow account)
    {
        ArgumentNullException.ThrowIfNull(account);
        return account switch
        {
            { IsActive: false } => ("отключена", Color.Error),
            { HasLocalPassword: false } => ("без локального пароля", Color.Warning),
            { MustChangePassword: true } => ("временный пароль", Color.Info),
            _ => ("активна", Color.Success),
        };
    }

    /// <summary>Коротко о допуске: «Секретно · подразделений: 2»; нет допуска — <see langword="null"/>.</summary>
    public static string? Clearance(UserAccountRow account)
    {
        ArgumentNullException.ThrowIfNull(account);
        return account.MaxClassification is { } level
            ? $"{ClassificationLevels.Label(level)} · подразделений: {(account.Divisions ?? []).Count.ToString(CultureInfo.InvariantCulture)}"
            : null;
    }

    /// <summary>Инициалы для кружка-аватара: «Ашыров Бактилек» → «АБ»; без ФИО — первая буква логина.</summary>
    public static string Initials(UserAccountRow account)
    {
        ArgumentNullException.ThrowIfNull(account);
        var words = (account.DisplayName ?? string.Empty)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var initials = string.Concat(words.Take(2).Select(w => char.ToUpperInvariant(w[0])));
        return initials.Length > 0 ? initials : account.UserName[..1].ToUpperInvariant();
    }

    /// <summary>Как называть сотрудника в текстах: ФИО, а без него — имя входа.</summary>
    public static string NameOf(UserAccountRow account)
    {
        ArgumentNullException.ThrowIfNull(account);
        return string.IsNullOrWhiteSpace(account.DisplayName) ? account.UserName : account.DisplayName;
    }

    /// <summary>Цвет точки события в истории сотрудника.</summary>
    public static Color HistoryColor(UserHistoryKind kind) => kind switch
    {
        UserHistoryKind.Success => Color.Success,
        UserHistoryKind.Warning => Color.Warning,
        UserHistoryKind.Danger => Color.Error,
        _ => Color.Primary,
    };

    /// <summary>Имя входа допустимо (то же правило, что у валидатора создания учётной записи).</summary>
    public static bool IsValidLogin(string? login) =>
        !string.IsNullOrWhiteSpace(login)
        && login.Trim().Length <= 100
        && login.Trim().All(c => c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '.' or '_' or '-');
}
