using ISC.AI.Profile.Inspector.Domain.Services;

namespace ISC.AI.Profile.Inspector.Application.Features.Loading;

/// <summary>
/// Единая проверка подразделения при наполнении корпуса (загрузка файла, импорт пакета): материал
/// принимается только под ДЕЙСТВУЮЩЕЕ подразделение из справочника.
/// </summary>
/// <remarks>
/// Почему проверка здесь, а не в ядре: ядро справочника подразделений не знает (он — домен профиля,
/// ADR-0013); ядру подразделение — число в решётке доступа. Случай, от которого защищает: пакет ЦБД
/// был собран вне контура с подразделением 10, которого в справочнике нет, — 100 НПА легли в корпус
/// и их не видел никто (решётка отсекала их у каждого пользователя, 2026-08-19). Теперь такой ввод
/// отклоняется с понятным текстом, а не принимается молча.
/// </remarks>
public static class DivisionRule
{
    /// <summary>Текст отказа (единый для файла и пакета).</summary>
    public static string Missing(int divisionId) =>
        $"Подразделения №{divisionId} нет в справочнике (или оно выведено из обращения). "
        + "Выберите подразделение из списка — материал под неизвестным подразделением не увидит никто.";

    /// <summary>Существует ли действующее подразделение.</summary>
    public static Task<bool> ExistsAsync(
        IDivisionAdminStore divisions, int divisionId, CancellationToken cancellationToken) =>
        divisions.ExistsActiveAsync(divisionId, cancellationToken);
}
