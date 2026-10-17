using System;
using System.Collections.Generic;
using System.Globalization;
using ISC.AI.Modules.Media.Domain.Model;
using ISC.AI.Modules.Media.Domain.Services;
using MudBlazor;

namespace ISC.AI.Modules.Media.UI;

/// <summary>
/// Русские подписи и цвета перечислений пакета «Медиа» для интерфейса. Формулировки — по ТЭ-005:
/// до двойного подтверждения только «кандидат»; после — «следственная версия», а не «установлен».
/// </summary>
public static class MediaLabels
{
    /// <summary>Подпись состояния индексации носителя (ТП-004).</summary>
    public static string Label(this MediaIndexStatus status) => status switch
    {
        MediaIndexStatus.Uploaded => "загружен, ждёт обработки",
        MediaIndexStatus.Processing => "обрабатывается",
        MediaIndexStatus.Indexed => "проиндексирован",
        MediaIndexStatus.Failed => "ошибка обработки",
        MediaIndexStatus.NotApplicable => "поиск по лицу неприменим",
        _ => status.ToString(),
    };

    /// <summary>Цвет чипа состояния индексации.</summary>
    public static Color ChipColor(this MediaIndexStatus status) => status switch
    {
        MediaIndexStatus.Uploaded => Color.Default,
        MediaIndexStatus.Processing => Color.Info,
        MediaIndexStatus.Indexed => Color.Success,
        MediaIndexStatus.Failed => Color.Error,
        _ => Color.Default,
    };

    /// <summary>Индексация ещё не завершена — список стоит перезапрашивать.</summary>
    public static bool IsInProgress(this MediaIndexStatus status) =>
        status is MediaIndexStatus.Uploaded or MediaIndexStatus.Processing;

    /// <summary>Подпись вида носителя.</summary>
    public static string Label(this MediaKind kind) => kind switch
    {
        MediaKind.Image => "фото",
        MediaKind.Video => "видео",
        MediaKind.Audio => "аудио",
        _ => kind.ToString(),
    };

    /// <summary>Значок вида носителя (список носителей дела, выдача поиска по расшифровкам).</summary>
    public static string Icon(this MediaKind kind) => kind switch
    {
        MediaKind.Image => Icons.Material.Filled.Image,
        MediaKind.Video => Icons.Material.Filled.Movie,
        MediaKind.Audio => Icons.Material.Filled.Audiotrack,
        _ => Icons.Material.Filled.InsertDriveFile,
    };

    /// <summary>Подпись состояния расшифровки речи (ADR-0026).</summary>
    public static string Label(this TranscriptStatus status) => status switch
    {
        TranscriptStatus.NotApplicable => "расшифровка неприменима",
        TranscriptStatus.Pending => "в очереди на расшифровку",
        TranscriptStatus.Processing => "идёт расшифровка",
        TranscriptStatus.Done => "расшифровано",
        TranscriptStatus.Failed => "ошибка расшифровки",
        _ => status.ToString(),
    };

    /// <summary>Цвет чипа состояния расшифровки.</summary>
    public static Color ChipColor(this TranscriptStatus status) => status switch
    {
        TranscriptStatus.Processing => Color.Info,
        TranscriptStatus.Done => Color.Success,
        TranscriptStatus.Failed => Color.Error,
        _ => Color.Default,
    };

    /// <summary>Расшифровка ещё не завершена (в очереди или выполняется) — её состояние стоит перезапросить.</summary>
    public static bool IsInProgress(this TranscriptStatus status) =>
        status is TranscriptStatus.Pending or TranscriptStatus.Processing;

    /// <summary>
    /// Подпись статуса кандидата (ТБ-073, ТЭ-005): «подтверждён» — только решением человека, в режиме двух лиц — после двух
    /// решений (ADR-0054). Сколько подписей у конкретного кандидата, видно в его решениях, а не в статусе.
    /// </summary>
    public static string Label(this CandidateStatus status) => status switch
    {
        CandidateStatus.Candidate => "кандидат — ждёт решения",
        CandidateStatus.PendingVerifier => "ждёт верификатора",
        CandidateStatus.Confirmed => "подтверждён (следственная версия)",
        CandidateStatus.Rejected => "отклонён",
        CandidateStatus.Undetermined => "неопределённо — к руководителю",
        _ => status.ToString(),
    };

    /// <summary>Цвет чипа статуса кандидата.</summary>
    public static Color ChipColor(this CandidateStatus status) => status switch
    {
        CandidateStatus.Candidate => Color.Default,
        CandidateStatus.PendingVerifier => Color.Info,
        CandidateStatus.Confirmed => Color.Success,
        CandidateStatus.Rejected => Color.Dark,
        CandidateStatus.Undetermined => Color.Warning,
        _ => Color.Default,
    };

    /// <summary>Подпись режима подтверждения (ТФ-ВЕР-07, ADR-0054).</summary>
    public static string Label(this ConfirmationMode mode) => mode switch
    {
        ConfirmationMode.SingleOperator => "один оператор",
        ConfirmationMode.TwoPerson => "два лица",
        _ => mode.ToString(),
    };

    /// <summary>
    /// Правило подтверждения словами для пояснений на страницах (ТБ-073): по действующему режиму; режим неизвестен
    /// (настройки не прочитаны) — общая формулировка без обещания числа подписей.
    /// </summary>
    public static string ConfirmationRule(ConfirmationMode? mode) => mode switch
    {
        ConfirmationMode.SingleOperator =>
            "Статус «подтверждён» присваивается только решением сотрудника с правом верификации — один оператор (ТБ-073, ТФ-ВЕР-07).",
        ConfirmationMode.TwoPerson =>
            "Статус «подтверждён» присваивается только после независимых решений двух сотрудников (ТБ-073).",
        _ => "Статус «подтверждён» присваивается только решением человека (ТБ-073).",
    };

    /// <summary>Подпись исхода решения сотрудника.</summary>
    public static string Label(this VerificationVerdict verdict) => verdict switch
    {
        VerificationVerdict.Confirmed => "подтверждён",
        VerificationVerdict.Rejected => "отклонён",
        VerificationVerdict.Undetermined => "неопределённо",
        _ => verdict.ToString(),
    };

    /// <summary>Подпись стадии верификации.</summary>
    public static string Label(this VerificationStage stage) => stage switch
    {
        VerificationStage.Expert => "эксперт",
        VerificationStage.Verifier => "верификатор",
        VerificationStage.Supervisor => "руководитель",
        _ => stage.ToString(),
    };

    /// <summary>Подпись области поиска (ТФ-ПЛ-05).</summary>
    public static string Label(this SearchScopeKind scope) => scope switch
    {
        SearchScopeKind.CurrentCase => "текущее дело",
        SearchScopeKind.SelectedCases => "выбранные дела",
        SearchScopeKind.AllAccessibleCases => "все дела в пределах допуска",
        _ => scope.ToString(),
    };

    /// <summary>
    /// Строка о материале кандидата (ТФ-ПЛ-02): «снято 08.12.2022 · Материалы ОРМ · г. Бишкек»; без времени съёмки —
    /// «загружено …» (время съёмки не выдумывается). Местное время сотрудника; <paramref name="withTime"/> — с часами.
    /// </summary>
    public static string MaterialLine(this MaterialContext material, bool withTime = false)
    {
        ArgumentNullException.ThrowIfNull(material);

        var format = withTime ? "dd.MM.yyyy HH:mm" : "dd.MM.yyyy";
        var parts = new List<string>(3)
        {
            material.CapturedAt is { } captured
                ? "снято " + captured.ToLocalTime().ToString(format, CultureInfo.InvariantCulture)
                : "загружено " + DateTime.SpecifyKind(material.UploadedAtUtc, DateTimeKind.Utc).ToLocalTime().ToString(format, CultureInfo.InvariantCulture),
        };
        if (!string.IsNullOrWhiteSpace(material.Source))
        {
            parts.Add(material.Source.Trim());
        }

        if (!string.IsNullOrWhiteSpace(material.Place))
        {
            parts.Add(material.Place.Trim());
        }

        return string.Join(" · ", parts);
    }

    /// <summary>Таймкод кадра видео «м:сс.д» из миллисекунд.</summary>
    public static string Timecode(long milliseconds)
    {
        var span = TimeSpan.FromMilliseconds(milliseconds);
        return span.TotalHours >= 1
            ? span.ToString(@"h\:mm\:ss\.f", CultureInfo.InvariantCulture)
            : span.ToString(@"m\:ss\.f", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Место в записи «мм:сс» (от часа — «ч:мм:сс») для фрагментов расшифровки (ADR-0026): оператор сверяет
    /// его со шкалой проигрывателя, где десятых долей нет. Доли секунды отбрасываются: фрагмент с 1:05.9
    /// показан как 01:05, а перемотка идёт к точному началу фрагмента.
    /// </summary>
    public static string ClockTimecode(long milliseconds)
    {
        var span = TimeSpan.FromMilliseconds(Math.Max(0, milliseconds));
        return span.TotalHours >= 1
            ? ((int)span.TotalHours).ToString(CultureInfo.InvariantCulture) + ":" + span.ToString(@"mm\:ss", CultureInfo.InvariantCulture)
            : span.ToString(@"mm\:ss", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Точный таймкод покадрового просмотра «чч:мм:сс.ммм» (ADR-0028): момент записи с точностью до миллисекунды —
    /// так он входит в реквизиты снимка кадра и в журнал. Часы всегда двумя знаками (от 100 часов — сколько есть);
    /// отрицательное значение показывается как начало записи.
    /// </summary>
    public static string PreciseTimecode(long milliseconds)
    {
        var span = TimeSpan.FromMilliseconds(Math.Max(0, milliseconds));
        return ((long)span.TotalHours).ToString("00", CultureInfo.InvariantCulture)
            + ":" + span.ToString(@"mm\:ss\.fff", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Подпись «кадр № N при F к/с» для момента <paramref name="milliseconds"/> (ADR-0028): N — номер кадра по
    /// НАТИВНОЙ частоте (<see cref="VideoProbe.FrameIndexAt(long, double)"/>), а не индекс раскадровки.
    /// <see langword="null"/> — частота неизвестна (носитель загружен до ADR-0028 и не переиндексирован).
    /// </summary>
    public static string? FrameLabel(long milliseconds, double? frameRate)
    {
        if (frameRate is not { } fps || fps <= 0 || double.IsNaN(fps) || double.IsInfinity(fps))
        {
            return null;
        }

        var index = VideoProbe.FrameIndexAt(Math.Max(0, milliseconds), fps);
        return "кадр № " + index.ToString(CultureInfo.InvariantCulture)
            + " при " + fps.ToString("0.###", CultureInfo.InvariantCulture) + " к/с";
    }

    /// <summary>Привычные названия форматов, у которых подтип MIME на название не похож.</summary>
    private static readonly Dictionary<string, string> FormatNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["video/x-matroska"] = "MKV",
        ["video/quicktime"] = "MOV",
        ["video/x-msvideo"] = "AVI",
        ["video/3gpp"] = "3GP",
        ["audio/3gpp"] = "3GP",
        ["audio/mpeg"] = "MP3",
    };

    /// <summary>
    /// Короткое название формата по MIME-типу для подсказок («браузер не воспроизводит контейнер MKV»):
    /// известные — привычным именем (MKV/MOV/AVI/3GP), прочие — подтипом MIME заглавными буквами
    /// (<c>video/x-flv</c> → «FLV»). Текст подсказки не должен расходиться с типом самого носителя.
    /// </summary>
    public static string FormatName(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
        {
            return "неизвестного формата";
        }

        if (FormatNames.TryGetValue(contentType, out var known))
        {
            return known;
        }

        var subtype = contentType[(contentType.IndexOf('/', StringComparison.Ordinal) + 1)..];
        if (subtype.StartsWith("x-", StringComparison.OrdinalIgnoreCase))
        {
            subtype = subtype[2..];
        }

        return subtype.Length > 0 ? subtype.ToUpperInvariant() : contentType.ToUpperInvariant();
    }

    /// <summary>Человекочитаемый размер файла.</summary>
    public static string FormatSize(long bytes) => bytes switch
    {
        >= 1024 * 1024 => $"{bytes / (1024.0 * 1024.0):0.#} МБ",
        >= 1024 => $"{bytes / 1024.0:0.#} КБ",
        _ => $"{bytes} Б",
    };

    // --- Порог поиска по лицу (ТФ-ПЛ-06/08): оператор видит и задаёт СХОЖЕСТЬ в процентах, система хранит и применяет
    // косинусное РАССТОЯНИЕ — так считает индекс лиц (ADR-0019). Перевод — только шкалой SimilarityScale (ADR-0054), одной
    // для поля, выдачи, отборов и карточек.

    /// <summary>Подпись порога для выдачи и сессии: «схожесть от 85 %» либо «без порога» (сессии до ADR-0054).</summary>
    public static string SimilarityThreshold(SimilarityScale scale, double? maxCosineDistance)
    {
        ArgumentNullException.ThrowIfNull(scale);
        return maxCosineDistance is { } distance
            ? string.Create(CultureInfo.InvariantCulture, $"схожесть от {scale.ThresholdPercent(distance)} %")
            : "без порога";
    }

    /// <summary>
    /// Предустановка «высокий» для ползунка порога (ТФ-ПЛ-06): середина между рабочим порогом и 99 %. «Низкий» — нижняя
    /// граница, «средний» — рабочий порог Администратора.
    /// </summary>
    public static int HighThresholdPreset(int workingPercent) =>
        Math.Min(MediaSettings.MaxPercent, (workingPercent + MediaSettings.MaxPercent + 1) / 2);
}
