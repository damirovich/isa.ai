namespace ISC.AI.Profile.Investigation.Application.Features.Clearances;

/// <summary>Строка экрана допусков: пользователь, его допуск и разбор его подразделений.</summary>
public sealed record UserClearanceRow(
    int UserId,
    string DisplayName,
    short? MaxClassification,
    IReadOnlyList<ClearanceDivision> Divisions)
{
    /// <summary>Есть ли действующий допуск (иначе субъект не видит ни одного дела).</summary>
    public bool HasClearance => MaxClassification is not null;

    /// <summary>
    /// Номера подразделений, которых НЕТ в справочнике: такой номер выглядит как выданный доступ,
    /// но не даёт ничего — ни одного дела с таким подразделением в системе нет и не появится.
    /// </summary>
    public IReadOnlyList<int> UnknownDivisionIds => [.. Divisions.Where(d => !d.IsKnown).Select(d => d.Id)];
}
