namespace ISC.AI.Speech.Speakers;

/// <summary>
/// Разметка дикторов (секция <c>Speech:Speakers</c>, ADR-0062 v0.3): модель «голосовых отпечатков» с пином SHA-256,
/// число дикторов, порог сходства реплик и предел длины записи.
/// </summary>
/// <remarks>
/// Не задан путь модели — разметки моделью нет (расшифровка как прежде), стерео-записи по каналам размечаются всё
/// равно: это не требует моделей. Задан — пин обязателен и проверяется при каждом запуске, как у модели речи (ТИ-004).
/// </remarks>
/// <param name="EmbeddingPath">ONNX «отпечатков» (CAM++).</param>
/// <param name="EmbeddingSha256">Пин SHA-256 «отпечатков».</param>
/// <param name="Count">Число дикторов; 0 — по порогу.</param>
/// <param name="Threshold">Порог среднего косинусного сходства реплик для объединения в одного диктора.</param>
/// <param name="MaxMinutes">Длиннее — моделью не размечать.</param>
public sealed record SpeakerOptions(
    string EmbeddingPath,
    string EmbeddingSha256,
    int Count = 0,
    double Threshold = SpeakerOptions.DefaultThreshold,
    int MaxMinutes = SpeakerOptions.DefaultMaxMinutes)
{
    /// <summary>Порог по умолчанию — 0,4 (проверка 22.10.2026, ADR-0062 v0.3; калибруется на пилоте).</summary>
    public const double DefaultThreshold = 0.4;

    /// <summary>Наименьший допустимый порог.</summary>
    public const double MinThreshold = 0.05;

    /// <summary>Наибольший допустимый порог.</summary>
    public const double MaxThreshold = 0.95;

    /// <summary>Предел длины записи по умолчанию, минут.</summary>
    public const int DefaultMaxMinutes = 120;

    /// <summary>Наибольшее число дикторов, которое можно задать.</summary>
    public const int MaxCount = 20;

    /// <summary>Разметка моделью включена: задан путь модели.</summary>
    public bool ModelEnabled => !string.IsNullOrWhiteSpace(EmbeddingPath);
}
