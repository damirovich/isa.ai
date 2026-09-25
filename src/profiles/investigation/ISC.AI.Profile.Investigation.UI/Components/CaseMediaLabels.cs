using System.Globalization;
using ISC.AI.Modules.Media.Domain.Model;
using ISC.AI.Modules.Media.Domain.Services;
using MudBlazor;

namespace ISC.AI.Profile.Investigation.UI;

/// <summary>
/// Русские подписи и цвета носителей пакета «Медиа» для вкладки «Материалы» карточки дела. Свои, а не
/// <c>MediaLabels</c> пакета: на Media.UI профиль не ссылается (ADR-0022). Каждое значение перечислений
/// подписано явно — в интерфейс не должно попадать сырое имя вроде «NotApplicable».
/// </summary>
public static class CaseMediaLabels
{
    /// <summary>Подпись вида носителя: «Фото», «Видео», «Аудио» (ADR-0026).</summary>
    public static string Kind(MediaKind kind) => kind switch
    {
        MediaKind.Image => "Фото",
        MediaKind.Video => "Видео",
        MediaKind.Audio => "Аудио",
        _ => "Носитель",
    };

    /// <summary>Подпись состояния индексации лиц (ТП-004); у аудио — «поиск по лицу неприменим».</summary>
    public static string Index(MediaIndexStatus status) => status switch
    {
        MediaIndexStatus.Uploaded => "Загружен",
        MediaIndexStatus.Processing => "Обрабатывается",
        MediaIndexStatus.Indexed => "Проиндексирован",
        MediaIndexStatus.Failed => "Ошибка",
        MediaIndexStatus.NotApplicable => "Поиск по лицу неприменим",
        _ => "—",
    };

    /// <summary>Цвет чипа состояния индексации; «неприменимо» — нейтральный: это не сбой и не успех.</summary>
    public static Color IndexColor(MediaIndexStatus status) => status switch
    {
        MediaIndexStatus.Indexed => Color.Success,
        MediaIndexStatus.Failed => Color.Error,
        MediaIndexStatus.Processing => Color.Info,
        MediaIndexStatus.NotApplicable => Color.Default,
        _ => Color.Default,
    };

    /// <summary>
    /// Поиск по лицу к носителю применим. У аудиозаписи лиц нет и быть не может — число лиц у неё не «0», а
    /// прочерк: иначе карточка утверждала бы, что биометрия обрабатывалась (ADR-0026).
    /// </summary>
    public static bool FacesApplicable(MediaAssetRow asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        return asset.Kind != MediaKind.Audio && asset.IndexStatus != MediaIndexStatus.NotApplicable;
    }

    /// <summary>Число лиц на носителе либо «—», если поиск по лицу к нему неприменим.</summary>
    public static string FaceCount(MediaAssetRow asset) =>
        FacesApplicable(asset) ? asset.FaceCount.ToString(CultureInfo.InvariantCulture) : "—";

    /// <summary>Подпись состояния расшифровки речи (ADR-0026); у изображения — «—».</summary>
    public static string Transcript(TranscriptStatus status) => status switch
    {
        TranscriptStatus.NotApplicable => "—",
        TranscriptStatus.Pending => "В очереди",
        TranscriptStatus.Processing => "Расшифровывается",
        TranscriptStatus.Done => "Расшифровано",
        TranscriptStatus.Failed => "Ошибка",
        _ => "—",
    };

    /// <summary>Цвет чипа состояния расшифровки.</summary>
    public static Color TranscriptColor(TranscriptStatus status) => status switch
    {
        TranscriptStatus.Processing => Color.Info,
        TranscriptStatus.Done => Color.Success,
        TranscriptStatus.Failed => Color.Error,
        _ => Color.Default,
    };
}
