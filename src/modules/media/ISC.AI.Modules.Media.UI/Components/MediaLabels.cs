using System.Globalization;
using ISC.AI.Modules.Media.Domain.Model;
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
        _ => kind.ToString(),
    };

    /// <summary>Подпись статуса кандидата (ТБ-073, ТЭ-005): «подтверждён» — только после двух решений.</summary>
    public static string Label(this CandidateStatus status) => status switch
    {
        CandidateStatus.Candidate => "кандидат — ждёт эксперта",
        CandidateStatus.PendingVerifier => "ждёт верификатора",
        CandidateStatus.Confirmed => "подтверждён двумя сотрудниками",
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
        _ => stage.ToString(),
    };

    /// <summary>Подпись области поиска (ТФ-ПЛ-05).</summary>
    public static string Label(this SearchScopeKind scope) => scope switch
    {
        SearchScopeKind.CurrentCase => "текущее дело",
        SearchScopeKind.SelectedCases => "выбранные дела",
        SearchScopeKind.AllAccessibleCases => "все доступные дела",
        _ => scope.ToString(),
    };

    /// <summary>Таймкод кадра видео «м:сс.д» из миллисекунд.</summary>
    public static string Timecode(long milliseconds)
    {
        var span = TimeSpan.FromMilliseconds(milliseconds);
        return span.TotalHours >= 1
            ? span.ToString(@"h\:mm\:ss\.f", CultureInfo.InvariantCulture)
            : span.ToString(@"m\:ss\.f", CultureInfo.InvariantCulture);
    }

    /// <summary>Человекочитаемый размер файла.</summary>
    public static string FormatSize(long bytes) => bytes switch
    {
        >= 1024 * 1024 => $"{bytes / (1024.0 * 1024.0):0.#} МБ",
        >= 1024 => $"{bytes / 1024.0:0.#} КБ",
        _ => $"{bytes} Б",
    };
}
