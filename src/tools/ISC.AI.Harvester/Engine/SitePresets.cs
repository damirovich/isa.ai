namespace ISC.AI.Harvester.Engine;

/// <summary>
/// Встроенные пресеты источников (конфиг-данные, не код). Добавление источника = новая запись здесь
/// (или ручной ввод селекторов оператором), без нового класса-коннектора.
/// </summary>
public static class SitePresets
{
    /// <summary>Доступные пресеты.</summary>
    public static IReadOnlyList<SitePreset> All { get; } =
    [
        // gov.kg: список НПА → детальные страницы /ru/npa/s/{id}; заголовок и текст — на карточке. Статический HTML.
        new SitePreset(
            Name: "gov.kg — НПА (постановления)",
            SuggestedSeedUrl: "https://www.gov.kg/ru/npa?page=1",
            DocType: "постановление",
            Rules: new SiteRules(
                ItemLinkSelector: "a[href*='/npa/s/']",
                TitleSelector: "h2.section-name-title",
                BodySelector: ".section-npa")),

        // ЦБД Минюста КР — SPA: контент рисуется JavaScript, нужен headless-рендеринг (Э4-15).
        // ВНИМАНИЕ: селекторы ПРЕДВАРИТЕЛЬНЫЕ — калибруются оператором на отрисованном DOM живого сайта.
        // «Только действующие» задаётся на стороне источника (фильтр «Статус=Действует» в SeedUrl).
        // Предпочтительная альтернатива скрапингу — официальный API Минюста через Tunduk (см. Э4-15).
        new SitePreset(
            Name: "cbd.minjust.gov.kg — ЦБД Минюста (SPA, headless)",
            SuggestedSeedUrl: "https://cbd.minjust.gov.kg/list-docs/ru",
            DocType: "нпа",
            Rules: new SiteRules(
                ItemLinkSelector: "a[href*='/act/view/']",
                TitleSelector: "h1",
                BodySelector: "main",
                RenderMode: RenderMode.Headless)),
    ];
}
