using System;
using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace ISC.AI.Modules.Media.Application;

/// <summary>
/// Настройки поиска по лицу и раскадровки пакета «Медиа» (ТН-008, ТО-мат-05/06, ТФ-ПЛ-06). Читаются из
/// конфигурации один раз (singleton); значения по умолчанию безопасны: кандидат-лист 20 (5..50), порог
/// расстояния не задан (только ранжирование), а ОСЛАБИТЬ порог пользователь может не дальше
/// <see cref="MaxAllowedCosineDistance"/> — это предел эксплуатанта, не оператора.
/// </summary>
/// <param name="CandidateListSize">Ширина кандидат-листа по умолчанию (ТН-008: 20).</param>
/// <param name="MinCandidateListSize">Нижняя граница ширины, настраиваемой оператором (ТН-008: 5).</param>
/// <param name="MaxCandidateListSize">Верхняя граница ширины (ТН-008: 50).</param>
/// <param name="MaxCosineDistance">Порог отсечения по косинусному расстоянию по умолчанию; <see langword="null"/> — без порога.</param>
/// <param name="MaxAllowedCosineDistance">Предел, дальше которого оператор порог ослабить не может (ТФ-ПЛ-06).</param>
/// <param name="ProbeCopyMaxBytes">До какого размера копия пробного изображения кладётся в аудит (ТБ-072).</param>
/// <param name="SampleFps">Частота выборки кадров видео при индексации (ТО-мат-06).</param>
/// <param name="HnswEfSearch">Ширина обхода HNSW, с которой ищет слой данных (фиксируется в сессии, ТО-инф-12).</param>
/// <param name="AutoSuggestEnabled">Предлагать ли связи с фигурантами после индексации носителя (ТФ-ПЕР-09).</param>
/// <param name="AutoSuggestMaxCosineDistance">Порог расстояния для предложений системы — строже ручного поиска: в очередь идут только близкие совпадения.</param>
/// <param name="AutoSuggestCandidatesPerReference">Сколько ближайших лиц носителя предлагается на один эталон фигуранта.</param>
/// <param name="TrackMinSimilarity">Схожесть шаблонов, с которой лицо соседнего кадра продолжает трек (ТФ-ПЕР-02, ADR-0037).</param>
/// <param name="TrackMaxGapSeconds">Наибольший разрыв между кадрами одного трека, секунд (лицо пропало из кадра дольше — новый трек).</param>
public sealed record MediaSearchOptions(
    int CandidateListSize = MediaSearchOptions.DefaultCandidateListSize,
    int MinCandidateListSize = MediaSearchOptions.DefaultMinCandidateListSize,
    int MaxCandidateListSize = MediaSearchOptions.DefaultMaxCandidateListSize,
    double? MaxCosineDistance = null,
    double MaxAllowedCosineDistance = MediaSearchOptions.DefaultMaxAllowedCosineDistance,
    long ProbeCopyMaxBytes = MediaSearchOptions.DefaultProbeCopyMaxBytes,
    double SampleFps = MediaSearchOptions.DefaultSampleFps,
    int HnswEfSearch = MediaSearchOptions.DefaultHnswEfSearch,
    bool AutoSuggestEnabled = true,
    double AutoSuggestMaxCosineDistance = MediaSearchOptions.DefaultAutoSuggestMaxCosineDistance,
    int AutoSuggestCandidatesPerReference = MediaSearchOptions.DefaultAutoSuggestCandidatesPerReference,
    double TrackMinSimilarity = MediaSearchOptions.DefaultTrackMinSimilarity,
    double TrackMaxGapSeconds = MediaSearchOptions.DefaultTrackMaxGapSeconds)
{
    /// <summary>Ключ конфигурации: порог схожести шаблонов для продолжения трека лица в видео.</summary>
    public const string TrackMinSimilarityKey = "Media:Video:TrackMinSimilarity";

    /// <summary>Ключ конфигурации: наибольший разрыв между кадрами одного трека, секунд.</summary>
    public const string TrackMaxGapSecondsKey = "Media:Video:TrackMaxGapSeconds";

    /// <summary>
    /// Трек продолжается при схожести 0,5 и выше: одно лицо на соседних кадрах обычно заметно ближе, разные люди —
    /// заметно дальше. Предварительное значение до калибровки на пилоте (ТО-мат-08).
    /// </summary>
    public const double DefaultTrackMinSimilarity = 0.5;

    /// <summary>Разрыв до 3 с: при выборке 1 кадр/с лицо может пропасть на два кадра (поворот, заслонили) и остаться в треке.</summary>
    public const double DefaultTrackMaxGapSeconds = 3.0;

    /// <summary>Разрыв трека в миллисекундах — для трекера.</summary>
    public long TrackMaxGapMs => (long)Math.Round(TrackMaxGapSeconds * 1000);

    /// <summary>Ключ конфигурации: ширина кандидат-листа по умолчанию.</summary>
    public const string CandidateListSizeKey = "Media:Search:CandidateListSize";

    /// <summary>Ключ конфигурации: минимальная ширина кандидат-листа.</summary>
    public const string MinCandidateListSizeKey = "Media:Search:MinCandidateListSize";

    /// <summary>Ключ конфигурации: максимальная ширина кандидат-листа.</summary>
    public const string MaxCandidateListSizeKey = "Media:Search:MaxCandidateListSize";

    /// <summary>Ключ конфигурации: порог косинусного расстояния по умолчанию (пусто — без порога).</summary>
    public const string MaxCosineDistanceKey = "Media:Search:MaxCosineDistance";

    /// <summary>Ключ конфигурации: предельно допустимый порог косинусного расстояния.</summary>
    public const string MaxAllowedCosineDistanceKey = "Media:Search:MaxAllowedCosineDistance";

    /// <summary>Ключ конфигурации: предельный размер копии пробы в аудите, байт.</summary>
    public const string ProbeCopyMaxBytesKey = "Media:Search:ProbeCopyMaxBytes";

    /// <summary>Ключ конфигурации: частота выборки кадров видео.</summary>
    public const string SampleFpsKey = "Media:Video:SampleFps";

    /// <summary>Ключ ширины обхода HNSW — тот же, что читает слой данных (<c>Media:Search:HnswEfSearch</c>).</summary>
    public const string HnswEfSearchKey = "Media:Search:HnswEfSearch";

    /// <summary>ТН-008: кандидат-лист по умолчанию.</summary>
    public const int DefaultCandidateListSize = 20;

    /// <summary>ТН-008: нижняя граница.</summary>
    public const int DefaultMinCandidateListSize = 5;

    /// <summary>ТН-008: верхняя граница.</summary>
    public const int DefaultMaxCandidateListSize = 50;

    /// <summary>Предел ослабления порога по умолчанию (косинусное расстояние 0,8 ≈ схожесть 0,2).</summary>
    public const double DefaultMaxAllowedCosineDistance = 0.8;

    /// <summary>Копия пробы в аудите — до 4 МБ.</summary>
    public const long DefaultProbeCopyMaxBytes = 4L * 1024 * 1024;

    /// <summary>Один кадр в секунду (ТО-мат-06).</summary>
    public const double DefaultSampleFps = 1.0;

    /// <summary>Ширина обхода HNSW по умолчанию (совпадает с умолчанием слоя данных).</summary>
    public const int DefaultHnswEfSearch = 200;

    /// <summary>Ключ конфигурации: включены ли предложения связей с фигурантами (ТФ-ПЕР-09).</summary>
    public const string AutoSuggestEnabledKey = "Media:Search:AutoSuggest:Enabled";

    /// <summary>Ключ конфигурации: порог косинусного расстояния для предложений системы.</summary>
    public const string AutoSuggestMaxCosineDistanceKey = "Media:Search:AutoSuggest:MaxCosineDistance";

    /// <summary>Ключ конфигурации: сколько лиц носителя предлагать на один эталон.</summary>
    public const string AutoSuggestCandidatesPerReferenceKey = "Media:Search:AutoSuggest:CandidatesPerReference";

    /// <summary>
    /// Порог предложений по умолчанию — косинусное расстояние 0,5 (схожесть 0,5): строже ручного поиска, чтобы
    /// очередь не засорялась. Предварительное значение до калибровки на пилоте (ТО-мат-08).
    /// </summary>
    public const double DefaultAutoSuggestMaxCosineDistance = 0.5;

    /// <summary>На эталон — до пяти ближайших лиц носителя (в видео один человек встречается во многих кадрах).</summary>
    public const int DefaultAutoSuggestCandidatesPerReference = 5;

    /// <summary>
    /// Читает настройки из конфигурации; отсутствующие/мусорные значения заменяются умолчаниями, а
    /// границы кандидат-листа приводятся к согласованному виду (<c>1 ≤ Min ≤ Default ≤ Max</c>).
    /// Настроенный порог по умолчанию не может быть дальше предела — иначе предел был бы фикцией.
    /// </summary>
    public static MediaSearchOptions Read(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var min = Math.Max(1, ReadInt(configuration, MinCandidateListSizeKey, DefaultMinCandidateListSize));
        var max = Math.Max(min, ReadInt(configuration, MaxCandidateListSizeKey, DefaultMaxCandidateListSize));
        var size = Math.Clamp(ReadInt(configuration, CandidateListSizeKey, DefaultCandidateListSize), min, max);

        var maxAllowed = ReadDouble(configuration, MaxAllowedCosineDistanceKey) ?? DefaultMaxAllowedCosineDistance;
        var threshold = ReadDouble(configuration, MaxCosineDistanceKey);
        if (threshold is { } configured)
        {
            threshold = Math.Min(configured, maxAllowed);
        }

        var fps = ReadDouble(configuration, SampleFpsKey);

        // ТФ-ПЕР-09: порог предложений не может быть дальше предела эксплуатанта — иначе система предлагала бы
        // то, что оператору искать запрещено; выключение — только явным «false».
        var suggestThreshold = Math.Min(
            ReadDouble(configuration, AutoSuggestMaxCosineDistanceKey) ?? DefaultAutoSuggestMaxCosineDistance, maxAllowed);
        var suggestEnabled = !bool.TryParse(configuration[AutoSuggestEnabledKey], out var enabled) || enabled;
        var perReference = Math.Clamp(
            ReadInt(configuration, AutoSuggestCandidatesPerReferenceKey, DefaultAutoSuggestCandidatesPerReference), 1, max);

        return new MediaSearchOptions(
            CandidateListSize: size,
            MinCandidateListSize: min,
            MaxCandidateListSize: max,
            MaxCosineDistance: threshold,
            MaxAllowedCosineDistance: maxAllowed,
            ProbeCopyMaxBytes: ReadLong(configuration, ProbeCopyMaxBytesKey, DefaultProbeCopyMaxBytes),
            SampleFps: fps is { } f && f > 0 ? f : DefaultSampleFps,
            HnswEfSearch: ReadInt(configuration, HnswEfSearchKey, DefaultHnswEfSearch),
            AutoSuggestEnabled: suggestEnabled,
            AutoSuggestMaxCosineDistance: suggestThreshold,
            AutoSuggestCandidatesPerReference: perReference,
            TrackMinSimilarity: Math.Clamp(ReadDouble(configuration, TrackMinSimilarityKey) ?? DefaultTrackMinSimilarity, 0, 1),
            TrackMaxGapSeconds: ReadDouble(configuration, TrackMaxGapSecondsKey) is { } gap && gap > 0 ? gap : DefaultTrackMaxGapSeconds);
    }

    /// <summary>
    /// Порог предложений системы (ТФ-ПЕР-09): настроенный для предложений, но не мягче общего порога поиска, если
    /// тот строже, и не дальше предела эксплуатанта.
    /// </summary>
    public double EffectiveAutoSuggestMaxCosineDistance =>
        Math.Min(Math.Min(AutoSuggestMaxCosineDistance, MaxCosineDistance ?? double.MaxValue), MaxAllowedCosineDistance);

    /// <summary>Ширина кандидат-листа для запроса: запрошенная либо умолчание, зажатая в <c>[Min, Max]</c> (ТН-008).</summary>
    public int ClampTopK(int? requested) =>
        Math.Clamp(requested ?? CandidateListSize, MinCandidateListSize, MaxCandidateListSize);

    /// <summary>
    /// Действующий порог расстояния (ТФ-ПЛ-06): запрошенный, иначе настроенный; в любом случае НЕ дальше
    /// <see cref="MaxAllowedCosineDistance"/> — оператор порог ужесточить может, ослабить сверх предела — нет.
    /// <see langword="null"/> — только когда ни оператор, ни конфигурация порога не задают.
    /// </summary>
    public double? EffectiveMaxCosineDistance(double? requested)
    {
        var value = requested ?? MaxCosineDistance;
        return value is { } v ? Math.Min(v, MaxAllowedCosineDistance) : null;
    }

    private static int ReadInt(IConfiguration configuration, string key, int fallback) =>
        int.TryParse(configuration[key], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value > 0
            ? value
            : fallback;

    private static long ReadLong(IConfiguration configuration, string key, long fallback) =>
        long.TryParse(configuration[key], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value > 0
            ? value
            : fallback;

    private static double? ReadDouble(IConfiguration configuration, string key) =>
        double.TryParse(configuration[key], NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && value >= 0
            ? value
            : null;
}
