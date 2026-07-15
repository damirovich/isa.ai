using System.Text.RegularExpressions;
using ISC.AI.Abstractions.Grounding;

namespace ISC.AI.Profile.Inspector.Application.Grounding;

/// <summary>
/// Профильная канонизация ссылок/фрагментов НПА (инж-ТЗ §5.3.1.2, ТО-мат-02, ADR-0008): метод строже
/// строкового совпадения. Приводит ru-склонения и сокращения к канон-форме, чтобы «ст. 12», «статьи 12»
/// и «Статья 12» сопоставлялись как одно. Заменяет нейтральную заглушку <c>IdentityCitationNormalizer</c>.
/// </summary>
/// <remarks>
/// Применяется СИММЕТРИЧНО к ссылке из вывода и к тексту фрагмента (ядровой валидатор нормализует обе
/// стороны одним нормализатором — иначе контейнмент не сработает). НЕ ослабляет инвариант грунтовки
/// (ТБ-041): нормализация лишь готовит текст к сопоставлению; вердикт — за ядровым валидатором.
/// </remarks>
public sealed partial class NpaCitationNormalizer : ICitationNormalizer
{
    /// <inheritdoc />
    public string Normalize(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        // 1) регистр + схлопывание пробелов (нейтральная база, как у ядровой заглушки).
        var value = string.Join(' ', text.ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        // 2) доменные канон-замены (симметрично для ссылки и фрагмента).
        value = ArticlePattern().Replace(value, "статья $1");
        value = PointPattern().Replace(value, "пункт $1");
        value = PartPattern().Replace(value, "часть $1");
        value = NumberPattern().Replace(value, "№$1");
        value = CodePattern().Replace(value, "$1 кодекс");
        value = ConstitutionPattern().Replace(value, "конституция");

        return value;
    }

    // Замены выполняются по УЖЕ приведённому к нижнему регистру тексту, поэтому IgnoreCase не нужен.
    [GeneratedRegex(@"\b(?:стать[а-яё]*|ст)\.?\s*(\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex ArticlePattern();

    [GeneratedRegex(@"\b(?:пункт[а-яё]*|п)\.?\s*(\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex PointPattern();

    [GeneratedRegex(@"\b(?:част[а-яё]*|ч)\.?\s*(\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex PartPattern();

    [GeneratedRegex(@"(?:№|n[°º])\s*(\d[\d\-/]*)", RegexOptions.CultureInvariant)]
    private static partial Regex NumberPattern();

    [GeneratedRegex(@"\b(уголовн|гражданск|налогов|трудов|административн|земельн|таможенн|бюджетн|семейн|водн|лесн|воздушн)[а-яё]*\s+кодекс[а-яё]*", RegexOptions.CultureInvariant)]
    private static partial Regex CodePattern();

    [GeneratedRegex(@"\bконституци[а-яё]*", RegexOptions.CultureInvariant)]
    private static partial Regex ConstitutionPattern();
}
