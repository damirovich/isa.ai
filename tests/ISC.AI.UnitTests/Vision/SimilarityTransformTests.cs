using System;
using System.Linq;
using ISC.AI.Modules.Media.Domain.Model;
using ISC.AI.Vision.Onnx.Embedding;
using Shouldly;
using Xunit;

namespace ISC.AI.UnitTests.Vision;

/// <summary>
/// Выравнивание лица (ТО-мат-05): оценка преобразования подобия по пяти точкам должна ТОЧНО
/// восстанавливать заведомо известное преобразование (масштаб + поворот + сдвиг) — иначе шаблоны
/// от одного и того же лица разъедутся между кадрами.
/// </summary>
public sealed class SimilarityTransformTests
{
    [Fact(DisplayName = "Тождественное преобразование: шаблон в самого себя")]
    public void Identity_maps_template_to_itself()
    {
        var t = SimilarityTransform.Estimate(SimilarityTransform.SFaceTemplate, SimilarityTransform.SFaceTemplate);

        t.A.ShouldBe(1f, 1e-4f);
        t.B.ShouldBe(0f, 1e-4f);
        t.Tx.ShouldBe(0f, 1e-3f);
        t.Ty.ShouldBe(0f, 1e-3f);
    }

    [Fact(DisplayName = "Масштаб 2× + поворот 30° + сдвиг восстанавливаются точно по пяти точкам")]
    public void Scaled_rotated_shifted_points_recover_the_transform()
    {
        const float scale = 2f;
        var angle = MathF.PI / 6f;
        var a = scale * MathF.Cos(angle);
        var b = scale * MathF.Sin(angle);
        var expected = new SimilarityTransform(a, b, 15f, -7f);

        // Точки исходного «лица» — шаблон; цель — шаблон, пропущенный через известное преобразование.
        var source = SimilarityTransform.SFaceTemplate;
        var target = source.Select(expected.Apply).ToArray();

        var estimated = SimilarityTransform.Estimate(source, target);

        estimated.A.ShouldBe(expected.A, 1e-3f);
        estimated.B.ShouldBe(expected.B, 1e-3f);
        estimated.Tx.ShouldBe(expected.Tx, 1e-2f);
        estimated.Ty.ShouldBe(expected.Ty, 1e-2f);
        estimated.Scale.ShouldBe(scale, 1e-3f);
    }

    [Fact(DisplayName = "Вырожденные точки (все совпадают) — явная ошибка, не деление на ноль")]
    public void Degenerate_points_throw()
    {
        var same = Enumerable.Repeat(new FacePoint(10, 10), 5).ToArray();

        Should.Throw<ArgumentException>(() => SimilarityTransform.Estimate(same, SimilarityTransform.SFaceTemplate));
    }
}
