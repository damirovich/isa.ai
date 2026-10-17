using System;
using ISC.AI.Modules.Media.Domain.Model;
using Shouldly;
using Xunit;

namespace ISC.AI.UnitTests.Media;

/// <summary>
/// Шкала «балл модели ↔ проценты» (ТФ-ПЛ-08, ADR-0054): опорные точки, перевод в обе стороны, показ с округлением вниз
/// согласован с порогом, битая шкала — ошибка, а не молчаливая подмена.
/// </summary>
public sealed class SimilarityScaleTests
{
    private static readonly SimilarityScale Scale = SimilarityScale.Default;

    [Fact(DisplayName = "Шкала по умолчанию: порог модели 0,363 = 70 %, ноль = 0 %, полное совпадение = 100 %, ниже нуля — 0 %")]
    public void Default_points()
    {
        Scale.Points.ShouldBe([(0.363, 70.0)]);
        Scale.ToPercent(0.363).ShouldBe(70, 1e-9);
        Scale.ToPercent(0).ShouldBe(0);
        Scale.ToPercent(1).ShouldBe(100);
        Scale.ToPercent(-0.4).ShouldBe(0);
        Scale.ToPercent(0.1815).ShouldBe(35, 1e-9); // середина нижнего отрезка
        Scale.ToPercent(0.6815).ShouldBe(85, 1e-9); // середина верхнего отрезка
        Scale.Describe().ShouldBe("балл 0,363 = 70 %");
    }

    [Theory(DisplayName = "Процент → балл → процент возвращает исходное значение во всём диапазоне 40–99 %")]
    [InlineData(40)]
    [InlineData(70)]
    [InlineData(85)]
    [InlineData(99)]
    public void Round_trip(int percent)
    {
        Scale.ToPercent(Scale.ToSimilarity(percent)).ShouldBe(percent, 1e-9);
        Scale.ThresholdPercent(Scale.MaxCosineDistance(percent)).ShouldBe(percent);
    }

    [Fact(DisplayName = "Показ — с округлением вниз: кандидат на самом пороге — «85 %», чуть ниже — «84 %», а не «85 %»")]
    public void Display_is_floored_and_consistent_with_threshold()
    {
        var atThreshold = 1 - Scale.MaxCosineDistance(85);
        Scale.DisplayPercent(atThreshold).ShouldBe(85);
        Scale.DisplayPercent(atThreshold - 0.001).ShouldBe(84);
        Scale.DisplayPercent(0.99).ShouldBe(99);
        Scale.DisplayPercent(1).ShouldBe(100);
    }

    [Fact(DisplayName = "Точки уточняются конфигурацией: несколько точек, концы добавляются; пусто — умолчание")]
    public void Custom_points()
    {
        var custom = SimilarityScale.Parse(" 0.25:50 ; 0.5:80 ");
        custom.Points.ShouldBe([(0.25, 50.0), (0.5, 80.0)]);
        custom.ToPercent(0.375).ShouldBe(65, 1e-9);
        custom.ToSimilarity(90).ShouldBe(0.75, 1e-9);
        SimilarityScale.Parse(null).Points.ShouldBe(Scale.Points);
        SimilarityScale.Parse("  ").Points.ShouldBe(Scale.Points);
    }

    [Theory(DisplayName = "Битая шкала — FormatException с объяснением: не «балл:процент», вне пределов, не возрастает")]
    [InlineData("0.363")]
    [InlineData("0,363:70")]
    [InlineData("abc:70")]
    [InlineData("1.2:70")]
    [InlineData("0.4:100")]
    [InlineData("0.5:70; 0.4:80")]
    [InlineData("0.4:80; 0.5:70")]
    public void Broken_scale_throws(string points)
    {
        var exception = Should.Throw<FormatException>(() => SimilarityScale.Parse(points));
        exception.Message.ShouldStartWith("Шкала схожести:");
    }
}
