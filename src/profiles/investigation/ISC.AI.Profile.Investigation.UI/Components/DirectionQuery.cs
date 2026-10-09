using ISC.AI.Profile.Investigation.Domain.Enums;

namespace ISC.AI.Profile.Investigation.UI;

/// <summary>
/// Отдел ОН/ОУ в адресе списка дел (ТЭ-008, ADR-0039): стартовая страница открывает <c>/cases?direction=on</c> или
/// <c>?direction=ou</c>, а список берёт отсюда отбор по отделу. Это только отбор внутри допуска — подставленное в адрес
/// значение ничего не открывает (ТБ-020), поэтому неизвестное значение просто означает «все отделы».
/// </summary>
public static class DirectionQuery
{
    /// <summary>Имя параметра адреса.</summary>
    public const string Name = "direction";

    /// <summary>Значение параметра для отдела: «on» — ОН, «ou» — ОУ.</summary>
    public static string Code(CaseDirection direction) => direction switch
    {
        CaseDirection.Surveillance => "on",
        CaseDirection.Establishment => "ou",
        _ => direction.ToString(),
    };

    /// <summary>Адрес списка дел с отбором по отделу.</summary>
    public static string CasesUri(CaseDirection direction) => $"/cases?{Name}={Code(direction)}";

    /// <summary>Отдел из параметра адреса; пусто или неизвестное значение — <see langword="null"/> (все отделы).</summary>
    public static CaseDirection? Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "on" => CaseDirection.Surveillance,
        "ou" => CaseDirection.Establishment,
        _ => null,
    };
}
