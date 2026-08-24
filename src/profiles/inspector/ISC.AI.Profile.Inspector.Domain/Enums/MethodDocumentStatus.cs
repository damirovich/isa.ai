namespace ISC.AI.Profile.Inspector.Domain.Enums;

/// <summary>Статус методического документа в реестре (§5.2.9): черновик дорабатывают, утверждённым пользуются.</summary>
public enum MethodDocumentStatus
{
    /// <summary>Черновик — требует доработки/проверки человеком (ТБ-042).</summary>
    Draft = 0,

    /// <summary>Утверждена — выверенная редакция; правка текста возвращает в черновики.</summary>
    Approved = 1,
}

/// <summary>Подписи по-русски — в домене, чтобы страницы не держали копий (как UserRoleLabels).</summary>
public static class MethodDocumentStatusLabels
{
    /// <summary>Подпись значения.</summary>
    public static string Label(this MethodDocumentStatus value) => value switch
    {
        MethodDocumentStatus.Draft => "Черновик",
        MethodDocumentStatus.Approved => "Утверждена",
        _ => value.ToString(),
    };
}
