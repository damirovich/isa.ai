using ISC.AI.Abstractions.Entities;

namespace ISC.AI.Modules.Media.Data.Entities;

/// <summary>
/// Часть незавершённой индексации видео (<c>media.index_checkpoint</c>, ТНД-007, ADR-0064): лица полностью обработанных
/// кадров раскадровки <see cref="FirstFrameIndex"/>..<see cref="LastFrameIndex"/>, чтобы повтор после сбоя или
/// перезапуска не распознавал их заново.
/// </summary>
/// <remarks>
/// Служебная строка конвейера: ни поиск, ни карточки, ни отчёты её не читают — опубликованные лица и шаблоны живут
/// только в <c>face</c>/<c>face_template</c> и пишутся одной транзакцией (ТП-005). Поэтому режимных полей здесь нет: часть
/// не выдаётся ни одним запросом, а гриф носителя берётся при публикации. Шаблоны в части — биометрия, поэтому часть
/// снимается публикацией результата, сменой прогона, уничтожением носителя (каскад FK, ТБ-064) и регламентом закрытого
/// дела (ТБ-074). Имён вырезок в части нет: файлов, на которые ссылается только часть, не бывает (ADR-0064 п. 6).
/// </remarks>
public class MediaIndexCheckpoint : BaseEntity
{
    /// <summary>Носитель.</summary>
    public int AssetId { get; set; }

    /// <summary>
    /// Ключ прогона (SHA-256, hex): исходник, модели и настройки конвейера. Повтор берёт части только своего ключа.
    /// </summary>
    public required string RunKey { get; set; }

    /// <summary>Номер первого кадра раскадровки в части.</summary>
    public int FirstFrameIndex { get; set; }

    /// <summary>Номер последнего кадра раскадровки в части (все кадры диапазона обработаны полностью).</summary>
    public int LastFrameIndex { get; set; }

    /// <summary>Сколько лиц в части — для акта уничтожения без разбора JSON.</summary>
    public int FaceCount { get; set; }

    /// <summary>Лица части (рамка, точки, качество, шаблон, оценки кадра) — JSON, формат версионирован.</summary>
    public required string Faces { get; set; }

    /// <summary>Навигация к носителю.</summary>
    public MediaAsset? Asset { get; set; }
}
