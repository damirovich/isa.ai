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
public sealed record SiteRules(
    string ItemLinkSelector,
    string? TitleSelector = null,
    string? BodySelector = null,
    string? NextPageSelector = null,
    int MaxPages = 5);
