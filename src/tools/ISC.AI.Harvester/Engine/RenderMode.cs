namespace ISC.AI.Harvester.Engine;

/// <summary>
/// Режим получения HTML страницы источника (Э4-15). Определяет, КАК доставать HTML, не затрагивая
/// селекторную логику извлечения.
/// </summary>
public enum RenderMode
{
    /// <summary>Прямой HTTP-запрос без выполнения JavaScript (по умолчанию; быстро; статические сайты).</summary>
    Static,

    /// <summary>Загрузка через headless-браузер с выполнением JavaScript — для SPA (ЦБД Минюста и др.),
    /// где контент рисуется скриптами и в статическом HTML отсутствует.</summary>
    Headless,
}
