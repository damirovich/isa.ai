using System;
using System.Collections.Generic;

namespace ISC.AI.Evals.Speech;

/// <summary>
/// Итог выравнивания гипотезы (выхода модели) с эталоном по расстоянию Левенштейна: сколько единиц
/// (слов для WER, символов для CER) заменено, пропущено и вставлено (КИ-10).
/// </summary>
/// <remarks>
/// <para>Тождества любого выравнивания: <c>ReferenceLength = Hits + Substitutions + Deletions</c> и
/// <c>HypothesisLength = Hits + Substitutions + Insertions + Unscored</c>. Число ошибок — минимальное (расстояние
/// Левенштейна); разбиение на замены/удаления/вставки — одно из оптимальных выравниваний.</para>
/// <para>УЧАСТКИ <c>[неразборчиво]</c> (методика пилота, 4.6). Пометка в эталоне не входит в
/// <see cref="ReferenceLength"/> (знаменатель WER/CER), а единицы гипотезы, пришедшиеся на её место, — это
/// <see cref="Unscored"/>: не совпадения, не замены и не вставки. Человек не разобрал, что там сказано, и
/// судить модель на этом месте не по чему.</para>
/// </remarks>
/// <param name="ReferenceLength">Длина эталона (N) без пометок <c>[неразборчиво]</c>: слов или символов.</param>
/// <param name="HypothesisLength">Длина гипотезы (все единицы, включая <paramref name="Unscored"/>).</param>
/// <param name="Substitutions">Замены (S): единица эталона распознана как другая.</param>
/// <param name="Deletions">Удаления (D): единица эталона пропущена моделью.</param>
/// <param name="Insertions">Вставки (I): у модели лишняя единица, которой нет в эталоне.</param>
/// <param name="Unscored">Единицы гипотезы на месте участков <c>[неразборчиво]</c> — не оцениваются.</param>
public readonly record struct EditCounts(
    int ReferenceLength, int HypothesisLength, int Substitutions, int Deletions, int Insertions, int Unscored = 0)
{
    /// <summary>Совпадения (H): единицы эталона, распознанные верно.</summary>
    public int Hits => ReferenceLength - Substitutions - Deletions;

    /// <summary>Всего ошибок: S + D + I (расстояние Левенштейна; поглощённое пометками — не ошибки).</summary>
    public int Errors => Substitutions + Deletions + Insertions;

    /// <summary>
    /// Доля ошибок (S + D + I) / N — WER для слов, CER для символов. Может быть больше 1 (много вставок).
    /// Пустой эталон (N = 0: запись без речи или эталон целиком из пометок <c>[неразборчиво]</c>) —
    /// <see langword="null"/>: делить не на что. Лишние слова модели на записи без речи считаются отдельно,
    /// как «ложные слова» (<see cref="SpeechEvalNoSpeechSummary"/>), а не как WER.
    /// </summary>
    public double? Rate => ReferenceLength == 0 ? null : (double)Errors / ReferenceLength;

    /// <summary>Сумма счётчиков — для сводки по корпусу (Σ ошибок / Σ длины эталона).</summary>
    /// <param name="other">Счётчики другой записи.</param>
    public EditCounts Add(EditCounts other) => new(
        ReferenceLength + other.ReferenceLength,
        HypothesisLength + other.HypothesisLength,
        Substitutions + other.Substitutions,
        Deletions + other.Deletions,
        Insertions + other.Insertions,
        Unscored + other.Unscored);

    /// <summary>Сумма счётчиков нескольких записей; пустой набор — нули.</summary>
    /// <param name="items">Счётчики записей.</param>
    public static EditCounts Sum(IEnumerable<EditCounts> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        var total = default(EditCounts);
        foreach (var item in items)
        {
            total = total.Add(item);
        }

        return total;
    }
}
