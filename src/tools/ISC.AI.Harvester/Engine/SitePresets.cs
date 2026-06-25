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
        // gov.kg: список НПА → детальные страницы /ru/npa/s/{id}; заголовок и текст — на карточке.
        new SitePreset(
            Name: "gov.kg — НПА (постановления)",
            SuggestedSeedUrl: "https://www.gov.kg/ru/npa?page=1",
            DocType: "постановление",
            Rules: new SiteRules(
                ItemLinkSelector: "a[href*='/npa/s/']",
                TitleSelector: "h2.section-name-title",
                BodySelector: ".section-npa")),
    ];
}
