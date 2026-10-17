using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ISC.AI.Modules.Media.Domain.Model;

/// <summary>
/// Шкала «балл модели ↔ проценты схожести» (ТФ-ПЛ-08, ТЭ-006, ADR-0054) — ОДНА на систему: ею переводят порог
/// оператора в косинусное расстояние для индекса лиц и балл кандидата в проценты для экранов и выгрузок. Иначе поле
/// порога, выдача, отборы и карточка фигуранта говорили бы разное.
/// </summary>
/// <remarks>
/// <para>
/// Балл — косинусная схожесть шаблонов (1 − косинусное расстояние). Шкала кусочно-линейная по опорным точкам
/// «балл:процент»; концы 0 → 0 % и 1 → 100 % добавляются всегда, балл ниже нуля — 0 %. По умолчанию одна точка:
/// рекомендованный разработчиками модели SFace порог «тот же человек» (OpenCV, проверка на LFW) — балл 0,363 = 70 %.
/// Решение Заказчика от 17.10.2026; точки уточняются на пилоте (ТО-мат-08) конфигурацией, без правки кода.
/// </para>
/// <para>
/// Проценты — шкала показа, а не вероятность «это он»: подсказка у чипа схожести по-прежнему говорит, что это
/// вероятностная оценка, а не установление личности (ТЭ-006).
/// </para>
/// </remarks>
public sealed class SimilarityScale
{
    /// <summary>Опорные точки по умолчанию: порог модели 0,363 = 70 %.</summary>
    public const string DefaultPoints = "0.363:70";

    // Запас на погрешность вычислений при показе с округлением вниз: перевод «% → балл → %» не обязан вернуть ровно
    // целое, а кандидат на самом пороге должен показываться порогом, а не процентом ниже.
    private const double DisplayEpsilon = 1e-9;

    private readonly double[] _scores;
    private readonly double[] _percents;

    private SimilarityScale(double[] scores, double[] percents)
    {
        _scores = scores;
        _percents = percents;
    }

    /// <summary>Шкала по умолчанию (<see cref="DefaultPoints"/>).</summary>
    public static SimilarityScale Default { get; } = Parse(DefaultPoints);

    /// <summary>Внутренние опорные точки (без концов 0 и 1) — для пояснения на экранах и в журнале.</summary>
    public IReadOnlyList<(double Score, double Percent)> Points =>
        Enumerable.Range(1, _scores.Length - 2).Select(i => (_scores[i], _percents[i])).ToList();

    /// <summary>Шкала словами: «балл 0,363 = 70 %»; без внутренних точек — «балл × 100».</summary>
    public string Describe() =>
        _scores.Length == 2
            ? "балл × 100"
            : string.Join("; ", Points.Select(p => string.Create(
                CultureInfo.GetCultureInfo("ru-RU"), $"балл {p.Score:0.###} = {p.Percent:0.#} %")));

    /// <summary>
    /// Разбирает опорные точки «балл:процент» через «;» (точка — десятичный разделитель). Пусто — точки по умолчанию.
    /// </summary>
    /// <exception cref="FormatException">
    /// Точка не разбирается, лежит вне (0; 1) × (0; 100) или точки не возрастают строго по обеим осям. Шкала — правило
    /// порога (безопасность поиска), поэтому битая шкала останавливает запуск, а не заменяется молча.
    /// </exception>
    public static SimilarityScale Parse(string? text)
    {
        var source = string.IsNullOrWhiteSpace(text) ? DefaultPoints : text;
        var scores = new List<double> { 0 };
        var percents = new List<double> { 0 };
        foreach (var part in source.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var pair = part.Split(':', StringSplitOptions.TrimEntries);
            if (pair.Length != 2
                || !double.TryParse(pair[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var score)
                || !double.TryParse(pair[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var percent))
            {
                throw new FormatException($"Шкала схожести: точка «{part}» не в виде «балл:процент» (например 0.363:70).");
            }

            if (score is <= 0 or >= 1 || percent is <= 0 or >= 100)
            {
                throw new FormatException($"Шкала схожести: точка «{part}» вне допустимого — балл от 0 до 1, процент от 0 до 100 (без концов).");
            }

            if (score <= scores[^1] || percent <= percents[^1])
            {
                throw new FormatException($"Шкала схожести: точки должны строго возрастать и по баллу, и по проценту («{part}»).");
            }

            scores.Add(score);
            percents.Add(percent);
        }

        scores.Add(1);
        percents.Add(100);
        return new SimilarityScale([.. scores], [.. percents]);
    }

    /// <summary>Процент схожести по баллу модели (без округления), в пределах 0..100.</summary>
    public double ToPercent(double similarity) => Interpolate(_scores, _percents, Math.Clamp(similarity, 0, 1));

    /// <summary>
    /// Процент для показа — с округлением ВНИЗ: «на экране не ниже порога» ⇔ «прошёл порог». Кандидат 84,7 % при
    /// пороге 85 % не показывается как «85 %».
    /// </summary>
    public int DisplayPercent(double similarity) => (int)Math.Floor(ToPercent(similarity) + DisplayEpsilon);

    /// <summary>Балл модели, соответствующий проценту (в пределах 0..1).</summary>
    public double ToSimilarity(double percent) => Interpolate(_percents, _scores, Math.Clamp(percent, 0, 100));

    /// <summary>Порог косинусного расстояния индекса лиц для порога «схожесть не ниже, %».</summary>
    public double MaxCosineDistance(double percent) => 1 - ToSimilarity(percent);

    /// <summary>Порог «схожесть от, %» по записанному порогу расстояния (сессия поиска) — до целого процента.</summary>
    public int ThresholdPercent(double maxCosineDistance) =>
        (int)Math.Round(ToPercent(1 - maxCosineDistance), MidpointRounding.AwayFromZero);

    private static double Interpolate(double[] from, double[] to, double value)
    {
        for (var i = 1; i < from.Length; i++)
        {
            if (value <= from[i])
            {
                var share = (value - from[i - 1]) / (from[i] - from[i - 1]);
                return to[i - 1] + share * (to[i] - to[i - 1]);
            }
        }

        return to[^1];
    }
}
