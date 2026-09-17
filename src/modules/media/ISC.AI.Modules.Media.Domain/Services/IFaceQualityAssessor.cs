using ISC.AI.Modules.Media.Domain.Model;

namespace ISC.AI.Modules.Media.Domain.Services;

/// <summary>
/// Порт оценки качества лица (ТО-мат-07): размер (межзрачковое расстояние), уверенность детектора,
/// положение в кадре. Ниже порога — шаблон не создаётся; для пробного изображения — отказ с причиной.
/// Пороги — конфигурация, калибруются на пилоте.
/// </summary>
public interface IFaceQualityAssessor
{
    /// <summary>Оценивает лицо; <paramref name="imageWidth"/>/<paramref name="imageHeight"/> — размеры исходного кадра.</summary>
    FaceQuality Assess(DetectedFace face, int imageWidth, int imageHeight);
}
