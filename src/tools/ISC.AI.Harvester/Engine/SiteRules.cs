namespace ISC.AI.Harvester.Engine;

/// <summary>
/// Правила извлечения со структурированного сайта (CSS-селекторы) — КОНФИГ, не код (ADR-0015).
/// Новый источник описывается набором этих правил, без нового класса-коннектора. Селекторы поставляет
/// оператор (видит сайт), поэтому движок остаётся универсальным.
/// </summary>
/// <param name="ItemLinkSelector">Селектор ссылок на документы на странице списка (берётся <c>href</c>).</param>
/// <param name="TitleSelector">Селектор заголовка на странице документа (иначе — &lt;title&gt;).</param>
/// <param name="BodySelector">Селектор основного текста на странице документа (иначе — всё тело).</param>
/// <param name="NextPageSelector">Селектор ссылки «следующая страница» списка (пагинация); <see langword="null"/> — без пагинации.</param>
/// <param name="MaxPages">Верхний предел страниц списка за прогон (вежливость к источнику).</param>
/// <param name="RenderMode">Как получать HTML: <see cref="Engine.RenderMode.Static"/> (HTTP) или
/// <see cref="Engine.RenderMode.Headless"/> (браузер с JS — для SPA вроде ЦБД Минюста, Э4-15).</param>
/// <param name="ReadySelector">Для headless: селектор контентного узла, отрисовки которого дождаться перед снятием DOM.</param>
/// <param name="PageParam">Имя query-параметра пагинации для инкремента (напр. <c>page</c> → <c>?page=2,3…</c>),
/// когда «следующая страница» — это не ссылка, а параметр URL. <see langword="null"/> — использовать
/// <paramref name="NextPageSelector"/> или без пагинации.</param>
public sealed record SiteRules(
    string ItemLinkSelector,
    string? TitleSelector = null,
    string? BodySelector = null,
    string? NextPageSelector = null,
    int MaxPages = 100,
    RenderMode RenderMode = RenderMode.Static,
    string? ReadySelector = null,
    string? PageParam = null);
