using System;
using System.Collections.Generic;

namespace ISC.AI.Evals.Speech;

/// <summary>
/// Язык речи записи эталонного набора. Метрики считаются ОТДЕЛЬНО по каждому языку (КИ-03, КИ-10): провал
/// киргизского или смешанной речи не должен маскироваться средним по русскому.
/// </summary>
/// <remarks>
/// Контрольный файл без речи (методика пилота, 3.4) языка не имеет: в манифесте у него код
/// <see cref="SpeechEvalCodes.NoSpeechCode"/>, в модели — <see langword="null"/> вместо значения перечисления.
/// </remarks>
public enum SpeechLanguage
{
    /// <summary>Русская речь (код <c>ru</c>).</summary>
    Russian,

    /// <summary>Киргизская речь (код <c>ky</c> — код языка ISO 639-1, не код страны <c>kg</c>).</summary>
    Kyrgyz,

    /// <summary>
    /// Русская и киргизская речь вперемешку в одной записи (код <c>mixed</c>) — главный риск выбора модели:
    /// на такой речи GigaAM Multilingual никем не измерялась (ADR-0026).
    /// </summary>
    Mixed,
}

/// <summary>
/// Условия записи — вторая ось сводки: качество на допросе и по телефону различается кратно. Набор и смысл
/// кодов — по методике пилота (<c>docs/следствие/Пилот_расшифровки_речи.md</c>, 3.2); в манифесте
/// указывается одно ОСНОВНОЕ условие, остальное — в примечании.
/// </summary>
public enum RecordingCondition
{
    /// <summary>Запись телефонного разговора (код <c>phone</c>): узкая полоса 8 кГц, кодеки связи, два голоса.</summary>
    Phone,

    /// <summary>Голосовое сообщение мессенджера (код <c>voice</c>): сжатие ogg/opus, m4a, окружающий шум.</summary>
    Voice,

    /// <summary>Допрос, опрос, беседа в кабинете (код <c>interview</c>) — основной «чистый» случай.</summary>
    Interview,

    /// <summary>Диктофон или телефон на столе (код <c>dictaphone</c>): эхо помещения, запись сбоку.</summary>
    Dictaphone,

    /// <summary>Улица, транспорт, толпа, телевизор на фоне (код <c>street</c>).</summary>
    Street,

    /// <summary>Дальний микрофон (код <c>far</c>): камера наблюдения, съёмка телефоном издалека.</summary>
    Far,

    /// <summary>Наложение голосов (код <c>overlap</c>): перебивают, говорят одновременно.</summary>
    Overlap,

    /// <summary>Прочее (код <c>other</c>) — пояснить в примечании манифеста.</summary>
    Other,
}

/// <summary>Коды языков и условий в манифесте набора и в отчёте, их названия по-русски.</summary>
public static class SpeechEvalCodes
{
    /// <summary>
    /// Код «языка» контрольного файла без речи (тишина, музыка, шум — методика 3.4). Такая запись в WER/CER
    /// не входит: по ней считаются ложные слова и фрагменты, эталон у неё пуст или отсутствует.
    /// </summary>
    public const string NoSpeechCode = "-";

    /// <summary>Допустимые коды языка — для текста ошибки.</summary>
    public const string LanguageCodesHint = "ru | ky | mixed, для контрольного файла без речи — «-»";

    /// <summary>Допустимые коды условий — для текста ошибки.</summary>
    public const string ConditionCodesHint = "phone | voice | interview | dictaphone | street | far | overlap | other";

    private static readonly Dictionary<string, SpeechLanguage> Languages = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ru"] = SpeechLanguage.Russian,
        ["ky"] = SpeechLanguage.Kyrgyz,
        ["mixed"] = SpeechLanguage.Mixed,
    };

    private static readonly Dictionary<string, RecordingCondition> Conditions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["phone"] = RecordingCondition.Phone,
        ["voice"] = RecordingCondition.Voice,
        ["interview"] = RecordingCondition.Interview,
        ["dictaphone"] = RecordingCondition.Dictaphone,
        ["street"] = RecordingCondition.Street,
        ["far"] = RecordingCondition.Far,
        ["overlap"] = RecordingCondition.Overlap,
        ["other"] = RecordingCondition.Other,
    };

    // Частые ошибки в манифесте — подсказка к тексту ошибки. Молча такие коды НЕ принимаются: набор кодов
    // один, иначе группы отчёта разъехались бы («mix» и «mixed» — две разные строки сводки).
    private static readonly Dictionary<string, string> LanguageHints = new(StringComparer.OrdinalIgnoreCase)
    {
        ["kg"] = "киргизский — код языка ky; kg — код страны",
        ["mix"] = "смешанная речь — mixed",
        ["none"] = "контрольный файл без речи — «-»",
    };

    private static readonly Dictionary<string, string> ConditionHints = new(StringComparer.OrdinalIgnoreCase)
    {
        ["recorder"] = "диктофон или телефон на столе — dictaphone",
        ["messenger"] = "голосовое сообщение мессенджера — voice",
    };

    /// <summary>Разбирает код языка (<c>ru</c>, <c>ky</c>, <c>mixed</c>) без учёта регистра.</summary>
    /// <param name="code">Код из манифеста.</param>
    /// <param name="language">Язык, если код известен.</param>
    /// <remarks>Код контрольного файла без речи — отдельно, <see cref="IsNoSpeechCode"/>.</remarks>
    public static bool TryParseLanguage(string? code, out SpeechLanguage language) =>
        Languages.TryGetValue(code?.Trim() ?? string.Empty, out language);

    /// <summary>Код <see cref="NoSpeechCode"/> — контрольный файл без речи.</summary>
    /// <param name="code">Код из манифеста.</param>
    public static bool IsNoSpeechCode(string? code) =>
        string.Equals(code?.Trim(), NoSpeechCode, StringComparison.Ordinal);

    /// <summary>Разбирает код условий записи без учёта регистра.</summary>
    /// <param name="code">Код из манифеста.</param>
    /// <param name="condition">Условия, если код известен.</param>
    public static bool TryParseCondition(string? code, out RecordingCondition condition) =>
        Conditions.TryGetValue(code?.Trim() ?? string.Empty, out condition);

    /// <summary>Подсказка к неизвестному коду языка («kg» → «ky» и т. п.) или пустая строка.</summary>
    /// <param name="code">Код из манифеста.</param>
    public static string LanguageHint(string? code) =>
        LanguageHints.TryGetValue(code?.Trim() ?? string.Empty, out var hint) ? $" ({hint})" : string.Empty;

    /// <summary>Подсказка к неизвестному коду условий («recorder» → «dictaphone» и т. п.) или пустая строка.</summary>
    /// <param name="code">Код из манифеста.</param>
    public static string ConditionHint(string? code) =>
        ConditionHints.TryGetValue(code?.Trim() ?? string.Empty, out var hint) ? $" ({hint})" : string.Empty;

    /// <summary>Код языка для манифеста и CSV.</summary>
    /// <param name="language">Язык.</param>
    public static string ToCode(this SpeechLanguage language) => language switch
    {
        SpeechLanguage.Russian => "ru",
        SpeechLanguage.Kyrgyz => "ky",
        SpeechLanguage.Mixed => "mixed",
        _ => throw new ArgumentOutOfRangeException(nameof(language), language, "Неизвестный язык."),
    };

    /// <summary>Код языка записи; <see langword="null"/> (контрольный файл без речи) — <see cref="NoSpeechCode"/>.</summary>
    /// <param name="language">Язык или <see langword="null"/>.</param>
    public static string ToCode(this SpeechLanguage? language) => language is { } value ? value.ToCode() : NoSpeechCode;

    /// <summary>Код условий для манифеста и CSV.</summary>
    /// <param name="condition">Условия записи.</param>
    public static string ToCode(this RecordingCondition condition) => condition switch
    {
        RecordingCondition.Phone => "phone",
        RecordingCondition.Voice => "voice",
        RecordingCondition.Interview => "interview",
        RecordingCondition.Dictaphone => "dictaphone",
        RecordingCondition.Street => "street",
        RecordingCondition.Far => "far",
        RecordingCondition.Overlap => "overlap",
        RecordingCondition.Other => "other",
        _ => throw new ArgumentOutOfRangeException(nameof(condition), condition, "Неизвестные условия записи."),
    };

    /// <summary>Название языка по-русски для отчёта.</summary>
    /// <param name="language">Язык.</param>
    public static string ToDisplayName(this SpeechLanguage language) => language switch
    {
        SpeechLanguage.Russian => "русский",
        SpeechLanguage.Kyrgyz => "киргизский",
        SpeechLanguage.Mixed => "смешанная речь",
        _ => throw new ArgumentOutOfRangeException(nameof(language), language, "Неизвестный язык."),
    };

    /// <summary>Название условий по-русски для отчёта.</summary>
    /// <param name="condition">Условия записи.</param>
    public static string ToDisplayName(this RecordingCondition condition) => condition switch
    {
        RecordingCondition.Phone => "телефонный разговор",
        RecordingCondition.Voice => "голосовое сообщение",
        RecordingCondition.Interview => "допрос / беседа",
        RecordingCondition.Dictaphone => "диктофон / телефон на столе",
        RecordingCondition.Street => "улица / шум",
        RecordingCondition.Far => "дальний микрофон",
        RecordingCondition.Overlap => "наложение голосов",
        RecordingCondition.Other => "прочее",
        _ => throw new ArgumentOutOfRangeException(nameof(condition), condition, "Неизвестные условия записи."),
    };
}
