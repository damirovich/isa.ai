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
/// <see cref="FragmentReferences"/>: они не текст документа, а номер в запросе к модели.
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

    // Формы с якорем: «статья 12», «ст. 12», «пункт 3», «ч. 2», «№ 45-6/2026», кодексы, Конституция (ru).
    [GeneratedRegex(
        @"(?:стать[а-яё]*|ст\.)\s*\d+" +
        @"|(?:пункт[а-яё]*|п\.)\s*\d+" +
        @"|(?:част[а-яё]*|ч\.)\s*\d+" +
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

/// <summary>
/// Ссылки ответа на фрагменты («[Фрагмент N]», «во фрагменте N»): номер должен быть среди поданных модели (ADR-0051).
/// Номер вне поданных — выдуманный источник; ответ показывается с предупреждением, как и при непроверенной ссылке.
/// </summary>
public static partial class FragmentReferences
{
    /// <summary>Номера фрагментов, на которые ссылается ответ, по порядку первого упоминания, без повторов.</summary>
    public static IReadOnlyList<int> Find(string? answer)
    {
        if (string.IsNullOrWhiteSpace(answer))
        {
            return [];
        }

        var result = new List<int>();
        foreach (Match match in Pattern().Matches(answer))
        {
            if (int.TryParse(match.Groups[1].Value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var number)
                && !result.Contains(number))
            {
                result.Add(number);
            }
        }

        return result;
    }

    /// <summary>Номера, которых модели не подавали (вне 1…<paramref name="supplied"/>).</summary>
    public static IReadOnlyList<int> Unknown(string? answer, int supplied) =>
        Find(answer).Where(n => n < 1 || n > supplied).ToList();

    /// <summary>
    /// Ответ на куски для показа: обычный текст и ссылки на фрагменты (<c>Fragment</c> — номер; текст куска — ссылка как
    /// написана, вместе с квадратными скобками, если они есть: экран выделяет её, не переписывая).
    /// </summary>
    public static IReadOnlyList<(string Text, int? Fragment)> Split(string? answer)
    {
        if (string.IsNullOrEmpty(answer))
        {
            return [];
        }

        var parts = new List<(string Text, int? Fragment)>();
        var position = 0;
        foreach (Match match in Pattern().Matches(answer))
        {
            if (match.Index > position)
            {
                parts.Add((answer[position..match.Index], null));
            }

            parts.Add((match.Value, int.Parse(match.Groups[1].Value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture)));
            position = match.Index + match.Length;
        }

        if (position < answer.Length)
        {
            parts.Add((answer[position..], null));
        }

        return parts;
    }

    // «[Фрагмент 2]», «[фрагмент №2]», «во фрагменте 3»: скобки — часть ссылки, если стоят вплотную.
    [GeneratedRegex(@"(?:\[\s*)?фрагмент[а-яё]*\s*(?:№\s*)?(\d{1,4})(?:\s*\])?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();
}
