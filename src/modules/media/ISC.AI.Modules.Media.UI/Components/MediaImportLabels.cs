using ISC.AI.Modules.Media.Application.Features.Import;
using ISC.AI.Modules.Media.Domain.Model;
using MudBlazor;

namespace ISC.AI.Modules.Media.UI;

/// <summary>Подписи и цвета состояний импорта архива (ADR-0041) для экранов пакета.</summary>
public static class MediaImportLabels
{
    /// <summary>Подпись состояния задания.</summary>
    public static string Label(this MediaImportJobStatus status) => status switch
    {
        MediaImportJobStatus.Queued => "ждёт очереди",
        MediaImportJobStatus.Running => "идёт",
        MediaImportJobStatus.Stopped => "остановлено",
        MediaImportJobStatus.Completed => "завершено",
        _ => status.ToString(),
    };

    /// <summary>Цвет чипа состояния задания.</summary>
    public static Color ChipColor(this MediaImportJobStatus status) => status switch
    {
        MediaImportJobStatus.Running => Color.Info,
        MediaImportJobStatus.Stopped => Color.Warning,
        MediaImportJobStatus.Completed => Color.Success,
        _ => Color.Default,
    };

    /// <summary>Подпись состояния строки.</summary>
    public static string Label(this MediaImportItemStatus status) => MediaImportReport.StatusLabel(status);

    /// <summary>Цвет чипа состояния строки.</summary>
    public static Color ChipColor(this MediaImportItemStatus status) => status switch
    {
        MediaImportItemStatus.Accepted => Color.Success,
        MediaImportItemStatus.Duplicate => Color.Info,
        MediaImportItemStatus.Rejected => Color.Error,
        MediaImportItemStatus.InProgress => Color.Primary,
        _ => Color.Default,
    };

    /// <summary>Подпись подачи в обработку.</summary>
    public static string Label(this MediaImportProcessingState state) => MediaImportReport.ProcessingLabel(state);

    /// <summary>Доля, проценты (0–100).</summary>
    public static double Percent(long done, long total) => total > 0 ? Math.Min(100d, done * 100d / total) : 0d;
}
