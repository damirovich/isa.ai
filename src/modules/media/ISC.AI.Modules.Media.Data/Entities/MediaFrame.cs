using ISC.AI.Abstractions.Entities;

namespace ISC.AI.Modules.Media.Data.Entities;

/// <summary>
/// Кадр видео (<c>media.frame</c>, ТС-011), на котором найдено хотя бы одно лицо. Кадры без лиц
/// не хранятся (ТО-мат-06): их байты не нужны, а таймкод восстанавливается из индекса и fps.
/// Каскадно удаляется с носителем (FK внутри схемы).
/// </summary>
public class MediaFrame : BaseEntity
{
    /// <summary>Носитель-видео.</summary>
    public int AssetId { get; set; }

    /// <summary>Навигация к носителю.</summary>
    public MediaAsset? Asset { get; set; }

    /// <summary>Порядковый индекс кадра в раскадровке.</summary>
    public int Index { get; set; }

    /// <summary>Таймкод кадра от начала видео, мс.</summary>
    public long TimestampMs { get; set; }
}
