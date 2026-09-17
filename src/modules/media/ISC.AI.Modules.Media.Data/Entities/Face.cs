using ISC.AI.Abstractions.Entities;
using ISC.AI.Abstractions.Security;

namespace ISC.AI.Modules.Media.Data.Entities;

/// <summary>
/// Лицо, найденное детектором (<c>media.face</c>, ТС-012): рамка, пять опорных точек, балл, оценка
/// пригодности и вырезка для показа. Гриф/подразделение ДЕНОРМАЛИЗОВАНЫ с носителя (ТБ-020): любая
/// выборка лиц фильтруется по строке, без джойна. Каскадно удаляется с носителем и кадром.
/// </summary>
public class Face : AuditableEntity, IClassified
{
    /// <summary>Носитель.</summary>
    public int AssetId { get; set; }

    /// <summary>Навигация к носителю.</summary>
    public MediaAsset? Asset { get; set; }

    /// <summary>Кадр (видео); <see langword="null"/> — лицо на изображении.</summary>
    public int? FrameId { get; set; }

    /// <summary>Навигация к кадру.</summary>
    public MediaFrame? Frame { get; set; }

    /// <summary>Рамка: левый верхний угол и размеры в пикселях исходного изображения.</summary>
    public float BoxX { get; set; }

    /// <summary>См. <see cref="BoxX"/>.</summary>
    public float BoxY { get; set; }

    /// <summary>См. <see cref="BoxX"/>.</summary>
    public float BoxWidth { get; set; }

    /// <summary>См. <see cref="BoxX"/>.</summary>
    public float BoxHeight { get; set; }

    /// <summary>Пять опорных точек (глаза, нос, углы рта) в пикселях: x0,y0,...,x4,y4.</summary>
    public float[] Landmarks { get; set; } = [];

    /// <summary>Балл детектора (0..1).</summary>
    public float DetectionScore { get; set; }

    /// <summary>Оценка пригодности (0..1) и её итог (ТО-мат-07).</summary>
    public float QualityScore { get; set; }

    /// <summary>Пригодно ли лицо для построения надёжного шаблона.</summary>
    public bool QualityAcceptable { get; set; }

    /// <summary>Причина непригодности (если есть).</summary>
    public string? QualityReason { get; set; }

    /// <summary>Имя файла вырезки (категория <c>media-faces</c>), если сохранена.</summary>
    public string? CropStoredFileName { get; set; }

    /// <summary>Трек лица в видео (серия кадров одного человека), если построен.</summary>
    public int? TrackId { get; set; }

    /// <summary>Гриф (денормализован с носителя, ТБ-020). NOT NULL.</summary>
    public short Classification { get; set; }

    /// <summary>Подразделение (денормализовано с носителя, ТБ-020). NOT NULL.</summary>
    public int DivisionId { get; set; }
}
