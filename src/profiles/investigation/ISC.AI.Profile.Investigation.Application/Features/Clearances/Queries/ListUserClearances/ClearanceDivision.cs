namespace ISC.AI.Profile.Investigation.Application.Features.Clearances;

/// <summary>Подразделение из допуска: наименование либо признак «в справочнике такого номера нет».</summary>
public sealed record ClearanceDivision(int Id, string? Name)
{
    /// <summary>Есть ли такое подразделение в справочнике профиля (<c>investigation.division</c>).</summary>
    public bool IsKnown => Name is not null;
}
