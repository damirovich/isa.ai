namespace ISC.AI.Evals;

/// <summary>Recall@k для одного языка (КИ-03) — отдельное число, не усреднённое с другими языками.</summary>
/// <param name="Language">Язык (ru/ky/…).</param>
/// <param name="RecallAtK">Средний recall@k по случаям этого языка (0..1).</param>
/// <param name="Cases">Число эталонных случаев этого языка.</param>
public sealed record LanguageRecall(string Language, double RecallAtK, int Cases);
