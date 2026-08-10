using System.Text.RegularExpressions;
using ISC.AI.Abstractions.Grounding;

namespace ISC.AI.Profile.Inspector.Application.Features.Grounding;

/// <summary>
/// Профильный экстрактор ссылок НПА (инж-ТЗ §5.3.1.2, ТБ-040): находит в выводе модели ссылки-кандидаты
/// для грунтовки — статьи/пункты/части с номером, номера актов «№ N», кодексы, Конституцию. Заменяет
/// нейтральную заглушку ядра <c>NoCitationExtractor</c>.
/// </summary>
/// <remarks>
/// ИНВАРИАНТ (ТБ-041): профиль лишь ПОСТАВЛЯЕТ доменное извлечение; саму проверку (Confirmed/Unverified/
/// Superseded) ведёт ядровой <c>GroundingValidator</c> — экстрактор его не ослабляет. Извлекаются только
/// НАДЁЖНО грунтуемые формы (якорь — номер/имя кодекса), чтобы не порождать ложные «висячие» ссылки,
/// которые заблокировали бы валидный вывод. Возвращаются сырые совпадения; канонизацию для сопоставления
/// делает <see cref="NpaCitationNormalizer"/> (обе стороны — одним нормализатором).
/// </remarks>
public sealed partial class NpaCitationExtractor : ICitationExtractor
{
    /// <inheritdoc />
    public IReadOnlyList<string> Extract(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return [];
        }

        // Порядок сохраняем, повторы отсекаем (регистронезависимо).
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();

        foreach (Match match in CitationPattern().Matches(output))
        {
            var citation = CollapseWhitespace(match.Value);
            if (citation.Length > 0 && seen.Add(citation))
            {
                result.Add(citation);
            }
        }

        return result;
    }

    private static string CollapseWhitespace(string value) =>
        string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Trim();

    // Единый паттерн-«или» по надёжно грунтуемым формам НПА (ru). IgnoreCase — склонения/регистр.
    [GeneratedRegex(
        @"(?:стать[а-яё]*|ст\.)\s*\d+" +                                   // статья 12 / статьи 12 / ст. 12
        @"|(?:пункт[а-яё]*|п\.)\s*\d+" +                                   // пункт 3 / п. 3
        @"|(?:част[а-яё]*|ч\.)\s*\d+" +                                    // часть 2 / ч. 2
        @"|(?:№|N[°º])\s*\d[\d\-/]*" +                                     // № 45 / N° 45 / № 45-6
        @"|(?:уголовн|гражданск|налогов|трудов|административн|земельн|таможенн|бюджетн|семейн|водн|лесн|воздушн)[а-яё]*\s+кодекс[а-яё]*" +
        @"|конституци[а-яё]*",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CitationPattern();
}
