namespace ISC.AI.Profile.Inspector.Application.Common;

/// <summary>
/// Маркировка грифа для профиля «Инспектор»: числовой гриф ядра → текст на документе (режимная схема
/// грифов — на стороне профиля; ядро держит нейтральный числовой <c>Classification</c>).
/// </summary>
public static class ClassificationMarking
{
    /// <summary>Возвращает текстовую маркировку по числовому грифу (0 — открыто, 1 — ДСП, далее — общий).</summary>
    public static string For(short classification) => classification switch
    {
        <= 0 => "ОТКРЫТО",
        1 => "ДСП",
        _ => $"ГРИФ {classification}",
    };
}
