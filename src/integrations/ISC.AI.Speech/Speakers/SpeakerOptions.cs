namespace ISC.AI.Speech.Speakers;

/// <summary>
/// Разметка дикторов (секция <c>Speech:Speakers</c>, ADR-0062): модели сегментации и «голосовых отпечатков» с пинами
/// SHA-256, число дикторов, порог и предел длины записи.
/// </summary>
/// <remarks>
/// Не заданы пути моделей — разметки моделью нет (расшифровка как прежде), стерео-записи по каналам размечаются всё
/// равно: это не требует моделей. Заданы — пины обязательны и проверяются при каждом запуске, как у модели речи (ТИ-004).
/// </remarks>
/// <param name="SegmentationPath">ONNX сегментации (pyannote).</param>
/// <param name="SegmentationSha256">Пин SHA-256 сегментации.</param>
/// <param name="EmbeddingPath">ONNX «отпечатков» (CAM++).</param>
/// <param name="EmbeddingSha256">Пин SHA-256 «отпечатков».</param>
/// <param name="Count">Число дикторов; 0 — по порогу.</param>
/// <param name="Threshold">Порог кластеризации.</param>
/// <param name="MaxMinutes">Длиннее — моделью не размечать.</param>
public sealed record SpeakerOptions(
    string SegmentationPath,
    string SegmentationSha256,
    string EmbeddingPath,
    string EmbeddingSha256,
    int Count = 0,
    double Threshold = SpeakerOptions.DefaultThreshold,
    int MaxMinutes = SpeakerOptions.DefaultMaxMinutes)
{
    /// <summary>Порог по умолчанию (как в примерах sherpa-onnx; калибруется на пилоте).</summary>
    public const double DefaultThreshold = 0.5;

    /// <summary>Предел длины записи по умолчанию, минут.</summary>
    public const int DefaultMaxMinutes = 120;

    /// <summary>Наибольшее число дикторов, которое можно задать.</summary>
    public const int MaxCount = 20;

    /// <summary>Разметка моделью включена: заданы оба пути.</summary>
    public bool ModelEnabled => !string.IsNullOrWhiteSpace(SegmentationPath) && !string.IsNullOrWhiteSpace(EmbeddingPath);
}
