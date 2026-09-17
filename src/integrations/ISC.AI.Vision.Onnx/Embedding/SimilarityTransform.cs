using ISC.AI.Modules.Media.Domain.Model;

namespace ISC.AI.Vision.Onnx.Embedding;

/// <summary>
/// Преобразование подобия (масштаб + поворот + сдвиг, без отражения) по методу наименьших квадратов —
/// выравнивание лица по пяти ключевым точкам под шаблон векторизатора (ТО-мат-05). Чистая математика,
/// тестируется без растров: <c>u = a·x − b·y + tx</c>, <c>v = b·x + a·y + ty</c>.
/// </summary>
/// <param name="A">Коэффициент a (масштаб·cos).</param>
/// <param name="B">Коэффициент b (масштаб·sin).</param>
/// <param name="Tx">Сдвиг по X.</param>
/// <param name="Ty">Сдвиг по Y.</param>
public readonly record struct SimilarityTransform(float A, float B, float Tx, float Ty)
{
    /// <summary>
    /// Стандартные позиции пяти точек на выровненном лице 112×112 (шаблон ArcFace/SFace, как в
    /// OpenCV <c>FaceRecognizerSF::alignCrop</c>): глаз слева, глаз справа, нос, уголки рта.
    /// </summary>
    public static readonly FacePoint[] SFaceTemplate =
    [
        new(38.2946f, 51.6963f), new(73.5318f, 51.5014f), new(56.0252f, 71.7366f),
        new(41.5493f, 92.3655f), new(70.7299f, 92.2041f),
    ];

    /// <summary>Оценивает преобразование, переводящее точки <paramref name="source"/> в <paramref name="target"/>.</summary>
    public static SimilarityTransform Estimate(IReadOnlyList<FacePoint> source, IReadOnlyList<FacePoint> target)
    {
        if (source.Count != target.Count || source.Count < 2)
        {
            throw new ArgumentException("Нужно не меньше двух пар точек одинакового количества.");
        }

        var n = source.Count;
        float mx = 0, my = 0, mu = 0, mv = 0;
        for (var i = 0; i < n; i++)
        {
            mx += source[i].X; my += source[i].Y; mu += target[i].X; mv += target[i].Y;
        }

        mx /= n; my /= n; mu /= n; mv /= n;

        // Центрированные суммы: a = Σ(dx·du + dy·dv)/Σ(dx²+dy²), b = Σ(dx·dv − dy·du)/Σ(dx²+dy²).
        float num = 0, cross = 0, denom = 0;
        for (var i = 0; i < n; i++)
        {
            var dx = source[i].X - mx;
            var dy = source[i].Y - my;
            var du = target[i].X - mu;
            var dv = target[i].Y - mv;
            num += dx * du + dy * dv;
            cross += dx * dv - dy * du;
            denom += dx * dx + dy * dy;
        }

        if (denom <= float.Epsilon)
        {
            throw new ArgumentException("Исходные точки вырождены (совпадают) — преобразование не определено.");
        }

        var a = num / denom;
        var b = cross / denom;
        var tx = mu - (a * mx - b * my);
        var ty = mv - (b * mx + a * my);
        return new SimilarityTransform(a, b, tx, ty);
    }

    /// <summary>Применяет преобразование к точке.</summary>
    public FacePoint Apply(FacePoint p) => new(A * p.X - B * p.Y + Tx, B * p.X + A * p.Y + Ty);

    /// <summary>Масштаб преобразования (√(a²+b²)).</summary>
    public float Scale => MathF.Sqrt(A * A + B * B);
}
