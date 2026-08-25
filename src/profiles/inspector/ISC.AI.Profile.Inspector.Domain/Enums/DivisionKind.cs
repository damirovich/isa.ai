namespace ISC.AI.Profile.Inspector.Domain.Enums;

/// <summary>
/// Тип подразделения (§4.2): территориальное (ТУ→РО) либо линейное. Нужен аналитике архива
/// (ТФ-АРХ-03 — сравнение территориальных и линейных) и справочнику «Подразделения».
/// </summary>
public enum DivisionKind
{
    /// <summary>Территориальное (управление/районный отдел).</summary>
    Territorial = 1,

    /// <summary>Линейное (по направлению деятельности).</summary>
    Linear = 2,
}

/// <summary>Русские подписи типов подразделений для интерфейса и факт-блоков.</summary>
public static class DivisionKindLabels
{
    /// <summary>Подпись типа.</summary>
    public static string Label(this DivisionKind value) => value switch
    {
        DivisionKind.Territorial => "Территориальное",
        DivisionKind.Linear => "Линейное",
        _ => value.ToString(),
    };
}
