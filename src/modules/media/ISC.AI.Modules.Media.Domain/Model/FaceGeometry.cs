namespace ISC.AI.Modules.Media.Domain.Model;

/// <summary>Прямоугольник лица в пикселях исходного изображения (левый верхний угол + размеры).</summary>
public readonly record struct BoundingBox(float X, float Y, float Width, float Height)
{
    /// <summary>Правая граница.</summary>
    public float Right => X + Width;

    /// <summary>Нижняя граница.</summary>
    public float Bottom => Y + Height;

    /// <summary>Площадь (для пересечений и NMS).</summary>
    public float Area => Math.Max(0f, Width) * Math.Max(0f, Height);
}

/// <summary>Точка на изображении в пикселях.</summary>
public readonly record struct FacePoint(float X, float Y);

/// <summary>
/// Пять ключевых точек лица в порядке детектора YuNet/ArcFace-шаблона: глаз слева на изображении
/// (правый глаз человека), глаз справа, кончик носа, левый и правый уголки рта. Порядок ВАЖЕН:
/// по нему считается выравнивание под векторизатор (ТО-мат-05).
/// </summary>
public readonly record struct FaceLandmarks(
    FacePoint LeftEye, FacePoint RightEye, FacePoint Nose, FacePoint LeftMouth, FacePoint RightMouth)
{
    /// <summary>Межзрачковое расстояние в пикселях — базовая мера размера лица для оценки качества.</summary>
    public float InterocularDistance =>
        MathF.Sqrt((RightEye.X - LeftEye.X) * (RightEye.X - LeftEye.X) + (RightEye.Y - LeftEye.Y) * (RightEye.Y - LeftEye.Y));

    /// <summary>Точки в порядке шаблона выравнивания.</summary>
    public FacePoint[] ToArray() => [LeftEye, RightEye, Nose, LeftMouth, RightMouth];
}

/// <summary>Результат детекции одного лица: рамка, ключевые точки, уверенность детектора (0..1).</summary>
public sealed record DetectedFace(BoundingBox Box, FaceLandmarks Landmarks, float Score);

/// <summary>
/// Оценка пригодности лица для векторизации (ТО-мат-07). Ниже порога шаблон не создаётся, лицо
/// помечается «непригодно» с причиной — оператор видит, почему, а не «тихо не нашлось».
/// </summary>
public sealed record FaceQuality(float Score, bool Acceptable, string? Reason);
