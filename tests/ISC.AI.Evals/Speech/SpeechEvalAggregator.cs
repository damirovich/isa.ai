using System;
using System.Collections.Generic;
using System.Linq;

namespace ISC.AI.Evals.Speech;

/// <summary>
/// Сводки прогона: по языку, по условиям записи и по их сочетанию, худшие записи, записи с большой долей
/// неразборчивого, записи «только для скорости», контрольные файлы без речи (КИ-10).
/// </summary>
/// <remarks>
/// <para>ОБЩЕГО WER «по всем языкам» здесь нет намеренно (как у <see cref="RecallCalculator"/>, КИ-03): русский
/// распознаётся заметно лучше, и общее число скрыло бы провал киргизского и смешанной речи. Сводка по
/// условиям смешивает языки — читать её вместе с таблицей «язык × условия».</para>
/// <para>Контрольные файлы без речи (язык «-») в сводки WER/CER не входят: у них нет слов эталона, а всё, что
/// выдала модель, — ложные слова. Они считаются отдельно (<see cref="NoSpeech"/>), иначе галлюцинации на
/// тишине растворились бы в WER группы как «вставки».</para>
/// <para>Записи «только для скорости» (<see cref="SpeechEvalRecording.IsSpeedOnly"/>: длинные записи целиком с
/// эталоном из одной пометки <c>[неразборчиво]</c>, методика 7.2) в сводки качества тоже не входят — ни в число
/// записей, ни в «Не оцен.»: десятки тысяч их слов модели дали бы ложную картину разметки. Их время,
/// длительность и RTF — отдельной сводкой (<see cref="SpeedOnly"/>).</para>
/// </remarks>
public static class SpeechEvalAggregator
{
    /// <summary>
    /// Ориентир методики (4.6): больше одной пометки <c>[неразборчиво]</c> на столько слов эталона — запись с
    /// большой долей неразборчивого (<see cref="UnintelligibleHeavy"/>).
    /// </summary>
    public const int WordsPerUnintelligibleMark = 20;

    /// <summary>Ключ сводки записей «только для скорости» (<see cref="SpeedOnly"/>).</summary>
    public const string SpeedOnlyKey = "all";

    /// <summary>Сводка по каждому языку — ВСЕ три языка, даже без записей: отсутствие ky в наборе должно быть видно.</summary>
    /// <param name="records">Итоги записей.</param>
    public static IReadOnlyList<SpeechEvalGroupSummary> ByLanguage(IEnumerable<SpeechEvalRecordResult> records)
    {
        ArgumentNullException.ThrowIfNull(records);

        var list = records.ToList();
        return Enum.GetValues<SpeechLanguage>()
            .Select(language => Summarize(
                language.ToCode(),
                language.ToDisplayName(),
                list.Where(record => record.Recording.Language == language)))
            .ToList();
    }

    /// <summary>Сводка по условиям записи — только встретившиеся в оцениваемых речевых записях, в порядке перечисления.</summary>
    /// <param name="records">Итоги записей.</param>
    public static IReadOnlyList<SpeechEvalGroupSummary> ByCondition(IEnumerable<SpeechEvalRecordResult> records)
    {
        ArgumentNullException.ThrowIfNull(records);

        return Scored(records)
            .GroupBy(record => record.Recording.Condition)
            .OrderBy(group => group.Key)
            .Select(group => Summarize(group.Key.ToCode(), group.Key.ToDisplayName(), group))
            .ToList();
    }

    /// <summary>Сводка по сочетаниям «язык/условия», встретившимся в оцениваемых речевых записях (ключ — <c>ky/phone</c>).</summary>
    /// <param name="records">Итоги записей.</param>
    public static IReadOnlyList<SpeechEvalGroupSummary> ByLanguageAndCondition(IEnumerable<SpeechEvalRecordResult> records)
    {
        ArgumentNullException.ThrowIfNull(records);

        return Scored(records)
            .GroupBy(record => (Language: record.Recording.Language!.Value, record.Recording.Condition)) // только речевые — язык задан
            .OrderBy(group => group.Key.Language)
            .ThenBy(group => group.Key.Condition)
            .Select(group => Summarize(
                $"{group.Key.Language.ToCode()}/{group.Key.Condition.ToCode()}",
                $"{group.Key.Language.ToDisplayName()}, {group.Key.Condition.ToDisplayName()}",
                group))
            .ToList();
    }

    /// <summary>
    /// Худшие оценённые речевые записи по WER (при равенстве — по числу ошибок); записи без ошибок не входят.
    /// Записи «только для скорости» и контрольные файлы без речи тоже не входят — они видны в своих таблицах.
    /// </summary>
    /// <param name="records">Итоги записей.</param>
    /// <param name="count">Сколько записей вернуть.</param>
    public static IReadOnlyList<SpeechEvalRecordResult> Worst(IEnumerable<SpeechEvalRecordResult> records, int count)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentOutOfRangeException.ThrowIfNegative(count);

        return Scored(records)
            .Where(record => record.WordErrorRate > 0)
            .OrderByDescending(record => record.WordErrorRate)
            .ThenByDescending(record => record.Words.Errors)
            .Take(count)
            .ToList();
    }

    /// <summary>
    /// Записи с большой долей неразборчивого (методика 4.6, протокол 10.1 §2): больше одной пометки
    /// <c>[неразборчиво]</c> на <see cref="WordsPerUnintelligibleMark"/> слов эталона. Их WER посчитан только по
    /// разобранной речи — качество модели на трудных местах не измерено. Доля считается по эталону, поэтому
    /// сбойные записи тоже входят; записи «только для скорости» не входят — у них нет слов эталона и доля не
    /// определена. Порядок — как в манифесте.
    /// </summary>
    /// <param name="records">Итоги записей.</param>
    public static IReadOnlyList<SpeechEvalRecordResult> UnintelligibleHeavy(IEnumerable<SpeechEvalRecordResult> records)
    {
        ArgumentNullException.ThrowIfNull(records);

        return Scored(records)
            .Where(record =>
            {
                var marks = record.Recording.UnintelligibleRegions;
                return marks > 0
                    && (long)marks * WordsPerUnintelligibleMark > SpeechTextNormalizer.CountScoredReferenceWords(record.Recording.ReferenceText);
            })
            .ToList();
    }

    /// <summary>
    /// Сводка по произвольной группе речевых записей: метрики по корпусу группы (Σ ошибок / Σ длины эталона).
    /// Контрольные файлы без речи и записи «только для скорости», если попали в группу, отбрасываются.
    /// </summary>
    /// <param name="key">Ключ группы.</param>
    /// <param name="displayName">Название по-русски.</param>
    /// <param name="records">Записи группы.</param>
    public static SpeechEvalGroupSummary Summarize(string key, string displayName, IEnumerable<SpeechEvalRecordResult> records)
    {
        ArgumentNullException.ThrowIfNull(records);

        return SummarizeCore(key, displayName, Scored(records).ToList());
    }

    /// <summary>
    /// Записи «только для скорости» (<see cref="SpeechEvalRecording.IsSpeedOnly"/>) одной сводкой по всем языкам:
    /// записей, сбоев, звук, время, RTF. WER/CER в ней не определены (слов эталона нет), <c>Words.Unscored</c> —
    /// все слова модели. <see langword="null"/> — таких записей в прогоне нет.
    /// </summary>
    /// <param name="records">Итоги записей.</param>
    public static SpeechEvalGroupSummary? SpeedOnly(IEnumerable<SpeechEvalRecordResult> records)
    {
        ArgumentNullException.ThrowIfNull(records);

        var list = records.Where(record => record.Recording.IsSpeedOnly).ToList();
        return list.Count == 0 ? null : SummarizeCore(SpeedOnlyKey, "только скорость", list);
    }

    /// <summary>
    /// Контрольные файлы без речи: ложные слова (все слова гипотезы) и ложные фрагменты (фрагменты с текстом),
    /// в сумме и на 10 минут звука; <see langword="null"/> — таких файлов в прогоне нет.
    /// </summary>
    /// <param name="records">Итоги записей.</param>
    public static SpeechEvalNoSpeechSummary? NoSpeech(IEnumerable<SpeechEvalRecordResult> records)
    {
        ArgumentNullException.ThrowIfNull(records);

        var list = records.Where(record => record.Recording.IsNoSpeech).ToList();
        if (list.Count == 0)
        {
            return null;
        }

        var evaluated = list.Where(record => record.Succeeded).ToList();

        // «На 10 минут» — только по длительности записи: измеренной по файлу или сообщённой распознавателем.
        // Конец последнего фрагмента на тишине ничего не говорит о длине записи.
        var measured = evaluated.Where(record => record.DurationSource is DurationSource.Measured or DurationSource.Transcriber).ToList();

        return new SpeechEvalNoSpeechSummary(
            Records: list.Count,
            Failed: list.Count - evaluated.Count,
            FalseWords: evaluated.Sum(record => record.Words.HypothesisLength),
            FalseFragments: evaluated.Sum(record => record.FragmentsWithText),
            MeasuredAudio: Sum(measured.Select(record => record.AudioDuration!.Value)), // Measured/Transcriber — длительность задана
            MeasuredFalseWords: measured.Sum(record => record.Words.HypothesisLength),
            MeasuredFalseFragments: measured.Sum(record => record.FragmentsWithText),
            UnmeasuredRecords: evaluated.Count - measured.Count);
    }

    private static SpeechEvalGroupSummary SummarizeCore(string key, string displayName, List<SpeechEvalRecordResult> list)
    {
        var evaluated = list.Where(record => record.Succeeded).ToList();

        // Скорость — только по оценённым записям, у которых время измерялось: время до сбоя о скорости
        // распознавания ничего не говорит, а у готового выхода сторонней модели его нет вовсе.
        var withAudio = evaluated.Where(record => record.AudioDuration is { } duration && duration > TimeSpan.Zero).ToList();
        var live = evaluated.Where(record => !record.Precomputed).ToList();
        var timed = withAudio.Where(record => !record.Precomputed).ToList();

        return new SpeechEvalGroupSummary(
            key,
            displayName,
            Records: list.Count,
            Failed: list.Count - evaluated.Count,
            Words: EditCounts.Sum(evaluated.Select(record => record.Words)),
            Characters: EditCounts.Sum(evaluated.Select(record => record.Characters)),
            Elapsed: live.Count == 0 ? null : Sum(live.Select(record => record.Elapsed)),
            Audio: Sum(withAudio.Select(record => record.AudioDuration!.Value)), // отобраны записи с длительностью
            TimedAudio: Sum(timed.Select(record => record.AudioDuration!.Value)),
            TimedElapsed: Sum(timed.Select(record => record.Elapsed)),
            DurationApproximate: timed.Any(record => record.DurationSource == DurationSource.LastSegmentEnd),
            UnintelligibleRegions: evaluated.Sum(record => record.Recording.UnintelligibleRegions));
    }

    // Записи, которые оцениваются по качеству: речевые и с эталоном, в котором есть что оценивать.
    private static IEnumerable<SpeechEvalRecordResult> Scored(IEnumerable<SpeechEvalRecordResult> records) =>
        records.Where(record => !record.Recording.IsNoSpeech && !record.Recording.IsSpeedOnly);

    private static TimeSpan Sum(IEnumerable<TimeSpan> values) =>
        values.Aggregate(TimeSpan.Zero, (total, value) => total + value);
}
