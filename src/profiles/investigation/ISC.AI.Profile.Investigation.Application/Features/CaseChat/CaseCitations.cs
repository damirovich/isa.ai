using System.Text.RegularExpressions;
using ISC.AI.Abstractions.Grounding;

namespace ISC.AI.Profile.Investigation.Application.Features.CaseChat;

/// <summary>
/// Ссылки в ответах по материалам дела (ТБ-040, ADR-0051): номера документов «№ N», статьи, пункты и части, кодексы,
/// Конституция. Заменяет нейтральную заглушку ядра <c>NoCitationExtractor</c> в профиле «Следствие».
/// </summary>
/// <remarks>
/// ИНВАРИАНТ ГРУНТОВКИ (ТБ-040/041, ТЗ ISC.AI инвариант 1). Профиль лишь ПОСТАВЛЯЕТ извлечение ссылок; проверку
/// (подтверждена / не подтверждена / утратила силу) ведёт ядровой <c>GroundingValidator</c> против ФАКТИЧЕСКИ поданных
/// модели фрагментов, и профиль её не ослабляет. Номер документа или статья, которых нет в поданных фрагментах, —
/// сказанное «по памяти»: ответ показывается с предупреждением. Извлекаются только формы с надёжным якорем (номер,
/// имя кодекса), чтобы не плодить ложных «висячих» ссылок. Ссылки на сами фрагменты («[Фрагмент N]») проверяет
/// пакет «Чат» (<c>FragmentReferences</c>, ADR-0052): они не текст документа, а номер в запросе к модели.
/// </remarks>
public sealed partial class CaseCitationExtractor : ICitationExtractor
{
    /// <inheritdoc />
    public IReadOnlyList<string> Extract(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return [];
        }

        // Порядок сохраняем, повторы отсекаем (без учёта регистра).
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (Match match in CitationPattern().Matches(output))
        {
            var citation = string.Join(' ', match.Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Trim();
            if (citation.Length > 0 && seen.Add(citation))
            {
                result.Add(citation);
            }
        }

        return result;
    }

    // Формы с якорем: «статья 12», «ст. 12», «пункт 3», «ч. 2», «№ 45-6/2026», кодексы, Конституция (ru). Якорь — с
    // начала слова, «часть» — только своими окончаниями: иначе адреса «участок 12», «ул. Частная 5», «пост. 3» давали бы
    // ложные «непроверенные ссылки», и сотрудник мог бы удалить верные сведения (ADR-0053).
    [GeneratedRegex(
        @"(?<![а-яёa-z])(?:стать[а-яё]*|ст\.)\s*\d+" +
        @"|(?<![а-яёa-z])(?:пункт[а-яё]*|п\.)\s*\d+" +
        @"|(?<![а-яёa-z])(?:част(?:ь|и|ью|ей|ям|ями|ях)|ч\.)\s*\d+" +
        @"|(?:№|N[°º])\s*\d[\d\-/]*" +
        @"|(?:уголовн|гражданск|налогов|трудов|административн|земельн|таможенн|бюджетн|семейн|водн|лесн|воздушн)[а-яё]*\s+(?:процессуальн[а-яё]*\s+)?кодекс[а-яё]*" +
        @"|конституци[а-яё]*",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CitationPattern();
}

/// <summary>
/// Канонизация ссылок и текста фрагментов для сопоставления (ADR-0051): «ст. 12», «статьи 12» и «Статья 12» — одно;
/// «№45», «№ 45» — одно. Применяется СИММЕТРИЧНО к ссылке и к фрагменту (ядровой валидатор нормализует обе стороны
/// одним нормализатором). Вердикт не меняет — только готовит текст (ТБ-041).
/// </summary>
public sealed partial class CaseCitationNormalizer : ICitationNormalizer
{
    /// <inheritdoc />
    public string Normalize(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var value = string.Join(' ', text.ToLowerInvariant().Replace('ё', 'е').Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        value = ArticlePattern().Replace(value, "статья $1");
        value = PointPattern().Replace(value, "пункт $1");
        value = PartPattern().Replace(value, "часть $1");
        value = NumberPattern().Replace(value, "№$1");
        value = CodePattern().Replace(value, "$1 кодекс");
        value = ConstitutionPattern().Replace(value, "конституция");
        return value;
    }

    // Замены — по уже приведённому к нижнему регистру тексту с «ё» → «е».
    [GeneratedRegex(@"\b(?:стать[а-я]*|ст)\.?\s*(\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex ArticlePattern();

    [GeneratedRegex(@"\b(?:пункт[а-я]*|п)\.?\s*(\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex PointPattern();

    [GeneratedRegex(@"\b(?:част[а-я]*|ч)\.?\s*(\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex PartPattern();

    [GeneratedRegex(@"(?:№|n[°º])\s*(\d[\d\-/]*)", RegexOptions.CultureInvariant)]
    private static partial Regex NumberPattern();

    [GeneratedRegex(@"\b(уголовн|гражданск|налогов|трудов|административн|земельн|таможенн|бюджетн|семейн|водн|лесн|воздушн)[а-я]*\s+(?:процессуальн[а-я]*\s+)?кодекс[а-я]*", RegexOptions.CultureInvariant)]
    private static partial Regex CodePattern();

    [GeneratedRegex(@"\bконституци[а-я]*", RegexOptions.CultureInvariant)]
    private static partial Regex ConstitutionPattern();
}
