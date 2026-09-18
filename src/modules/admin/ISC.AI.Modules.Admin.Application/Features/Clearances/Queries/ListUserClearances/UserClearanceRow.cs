namespace ISC.AI.Modules.Admin.Application.Features.Clearances;

/// <summary>Строка экрана допусков: пользователь, его допуск и разбор его подразделений.</summary>
/// <param name="UserId">Пользователь реестра ядра (<c>core.app_user</c>).</param>
/// <param name="DisplayName">Отображаемое имя.</param>
/// <param name="MaxClassification">Максимальный гриф допуска; <see langword="null"/> — допуска нет.</param>
/// <param name="Divisions">Разрешённые подразделения с наименованиями.</param>
public sealed record UserClearanceRow(
    int UserId,
    string DisplayName,
    short? MaxClassification,
    IReadOnlyList<ClearanceDivision> Divisions)
{
    /// <summary>Есть ли действующий допуск (иначе субъект не может ни читать, ни регистрировать).</summary>
    public bool HasClearance => MaxClassification is not null;

    /// <summary>
    /// Номера подразделений, которых НЕТ в справочнике. Это и есть тихая поломка, ради которой экран
    /// разбирает допуск, а не показывает голый массив: такой номер выглядит как выданный доступ, но не
    /// даёт ничего — ни одного документа с таким подразделением в системе нет и не появится.
    /// </summary>
    public IReadOnlyList<int> UnknownDivisionIds => [.. Divisions.Where(d => !d.IsKnown).Select(d => d.Id)];
}
