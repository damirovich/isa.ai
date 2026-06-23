namespace ISC.AI.Abstractions.Enums;

/// <summary>
/// Канонические роли моделей (keyed-регистрация). Ядро оперирует только ролью;
/// конкретные веса/квантизация задаются конфигурацией под бюджет VRAM (ТО-прог-03).
/// </summary>
public enum ModelRole
{
    /// <summary>Быстрая генерация и форматирование документов.</summary>
    Draft,

    /// <summary>Анализ и сверка документов; длинный контекст.</summary>
    Analysis,

    /// <summary>Векторизация текста (эмбеддинги) для семантического поиска (ru/ky).</summary>
    Embeddings,
}
