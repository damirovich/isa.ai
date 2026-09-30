using System.Globalization;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.Admin.Application.Features.Roles;

namespace ISC.AI.Modules.Admin.Application.Features.Accounts;

/// <summary>Окраска события в истории сотрудника: обычное, успешное, настораживающее, опасное.</summary>
public enum UserHistoryKind
{
    /// <summary>Обычное изменение.</summary>
    Neutral,

    /// <summary>Создание, включение.</summary>
    Success,

    /// <summary>Сброс пароля.</summary>
    Warning,

    /// <summary>Отключение, отзыв допуска.</summary>
    Danger,
}

/// <summary>Событие истории сотрудника.</summary>
/// <param name="OccurredAt">Когда (UTC).</param>
/// <param name="Who">Кто сделал (имя из реестра); <see langword="null"/> — неизвестно.</param>
/// <param name="What">Что произошло — по-русски.</param>
/// <param name="Kind">Окраска.</param>
public sealed record UserHistoryEntry(DateTime OccurredAt, string? Who, string What, UserHistoryKind Kind);

/// <summary>
/// Перевод записей журнала аудита (ТБ-030) о сотруднике на человеческий язык для карточки на экране «Пользователи».
/// Записи неизменяемы и хранят технические сводки — здесь они только читаются, не переписываются.
/// </summary>
/// <remarks>
/// Разбираются сводки сценариев пакета: <c>admin:account:{id}:…</c>, <c>admin:clearance:{id}:…</c>,
/// <c>admin:role:{id}:{ключ}</c>, <c>admin:account:create:{логин}</c>, смена собственного пароля; и прежние экраны
/// ролей профилей <c>{профиль}:user-role:{id}:{ключ}</c> — чтобы история не обрывалась на переходе к карточке.
/// Чужая или непонятная сводка — <see langword="null"/>: в историю не попадает.
/// </remarks>
public static class UserHistoryText
{
    /// <summary>Сводка смены собственного пароля (сценарий <c>ChangeOwnPasswordCommand</c>).</summary>
    public const string OwnPasswordChange = "admin:account:change-own-password";

    /// <summary>Событие, если сводка журнала относится к сотруднику; иначе <see langword="null"/>.</summary>
    /// <param name="objectRef">Сводка записи журнала.</param>
    /// <param name="subjectId">Кто сделал запись.</param>
    /// <param name="userId">Сотрудник карточки.</param>
    /// <param name="userName">Имя входа сотрудника (для записи о создании — в ней ещё нет номера).</param>
    /// <param name="roleLabels">Подписи ролей по ключу.</param>
    /// <param name="divisionNames">Наименования подразделений по номеру.</param>
    public static (string What, UserHistoryKind Kind)? Describe(
        string? objectRef,
        int? subjectId,
        int userId,
        string userName,
        IReadOnlyDictionary<string, string> roleLabels,
        IReadOnlyDictionary<int, string> divisionNames)
    {
        ArgumentNullException.ThrowIfNull(roleLabels);
        ArgumentNullException.ThrowIfNull(divisionNames);
        if (string.IsNullOrEmpty(objectRef))
        {
            return null;
        }

        var id = userId.ToString(CultureInfo.InvariantCulture);

        if (string.Equals(objectRef, "admin:account:create:" + userName, StringComparison.OrdinalIgnoreCase))
        {
            return ("Учётная запись создана, выдан временный пароль", UserHistoryKind.Success);
        }

        if (objectRef == OwnPasswordChange)
        {
            return subjectId == userId ? ("Сменил собственный пароль", UserHistoryKind.Neutral) : null;
        }

        if (TryTail(objectRef, $"admin:account:{id}:", out var account))
        {
            return account switch
            {
                "update-profile" => ("Изменены ФИО или должность", UserHistoryKind.Neutral),
                "reset-password" => ("Сброшен пароль, выдан временный", UserHistoryKind.Warning),
                "enable" => ("Учётная запись включена", UserHistoryKind.Success),
                "disable" => ("Учётная запись отключена", UserHistoryKind.Danger),
                _ => null,
            };
        }

        if (TryTail(objectRef, $"admin:clearance:{id}:", out var clearance))
        {
            return clearance == "revoke"
                ? ("Допуск отозван", UserHistoryKind.Danger)
                : DescribeClearance(clearance, divisionNames);
        }

        if (TryTail(objectRef, $"admin:role:{id}:", out var role) || TryRoleOfProfile(objectRef, id, out role))
        {
            return role is AssignUserRoleCommand.RemovedMarker
                ? ("Роль снята", UserHistoryKind.Neutral)
                : ("Роль: " + (roleLabels.TryGetValue(role, out var label) ? label : role), UserHistoryKind.Neutral);
        }

        return null;
    }

    // «set:grif=2;divisions=1,3» → «Допуск: Секретно; подразделения: 7Управление, …».
    private static (string, UserHistoryKind)? DescribeClearance(string tail, IReadOnlyDictionary<int, string> divisionNames)
    {
        if (!tail.StartsWith("set:", StringComparison.Ordinal))
        {
            return null;
        }

        string? grif = null;
        var divisions = new List<string>();
        foreach (var part in tail["set:".Length..].Split(';'))
        {
            if (part.StartsWith("grif=", StringComparison.Ordinal)
                && short.TryParse(part["grif=".Length..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var level))
            {
                grif = ClassificationLevels.Label(level);
            }
            else if (part.StartsWith("divisions=", StringComparison.Ordinal))
            {
                foreach (var raw in part["divisions=".Length..].Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    divisions.Add(int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
                        && divisionNames.TryGetValue(number, out var name) ? name : "№ " + raw);
                }
            }
        }

        var scope = divisions.Count == 0 ? "ни одного" : string.Join(", ", divisions);
        return ($"Допуск: {grif ?? "—"}; подразделения: {scope}", UserHistoryKind.Neutral);
    }

    // Прежние экраны ролей профилей: «investigation:user-role:{id}:{ключ}», «inspector:user-role:{id}:{ключ}».
    private static bool TryRoleOfProfile(string objectRef, string id, out string role)
    {
        var marker = $":user-role:{id}:";
        var at = objectRef.IndexOf(marker, StringComparison.Ordinal);
        if (at > 0 && objectRef.IndexOf(':', StringComparison.Ordinal) == at)
        {
            role = objectRef[(at + marker.Length)..];
            return role.Length > 0;
        }

        role = string.Empty;
        return false;
    }

    private static bool TryTail(string objectRef, string prefix, out string tail)
    {
        if (objectRef.StartsWith(prefix, StringComparison.Ordinal) && objectRef.Length > prefix.Length)
        {
            tail = objectRef[prefix.Length..];
            return true;
        }

        tail = string.Empty;
        return false;
    }
}
