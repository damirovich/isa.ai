using System;
using ISC.AI.Modules.Media.Domain.Model;
using ISC.AI.Modules.Media.Domain.Services;
using ISC.AI.Vision.Onnx.Imaging;
using Shouldly;
using SkiaSharp;
using Xunit;

namespace ISC.AI.UnitTests.Vision;

/// <summary>
/// Утилиты изображений (ТФ-ПЛ-02, ТЭ-007): размер читается из заголовка; вырезка лица расширяется полями,
/// обрезается по границам, ужимается по большей стороне и НЕ растягивается; на выходе — JPEG.
/// </summary>
public sealed class SkiaImageToolsTests
{
    private static byte[] Png(int width, int height)
    {
        using var bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Red);
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    [Fact(DisplayName = "ReadSize: размер PNG читается из заголовка; мусор — явная ошибка")]
    public void ReadSize_returns_dimensions()
    {
        var tools = new SkiaImageTools();
        tools.ReadSize(Png(200, 100)).ShouldBe(new ImageSize(200, 100));
        Should.Throw<InvalidOperationException>(() => tools.ReadSize([1, 2, 3, 4]));
    }

    [Fact(DisplayName = "CropJpeg: поля 25 %, обрезка по границам, ужатие до maxSide по большей стороне, выход — JPEG")]
    public void CropJpeg_applies_margin_clamp_and_downscale()
    {
        var tools = new SkiaImageTools();
        var source = Png(200, 100);

        // Рамка 40×40 в (50,20) + поля 10 → (40,10)-(100,70) = 60×60 → ужато до 32×32.
        var jpeg = tools.CropJpeg(source, new BoundingBox(50, 20, 40, 40), marginRatio: 0.25f, maxSide: 32, quality: 80);
        jpeg[0].ShouldBe((byte)0xFF);
        jpeg[1].ShouldBe((byte)0xD8); // SOI-маркер JPEG
        tools.ReadSize(jpeg).ShouldBe(new ImageSize(32, 32));

        // Рамка, выходящая за левый верхний угол, обрезается по изображению; маленькая вырезка НЕ растягивается.
        var clipped = tools.CropJpeg(source, new BoundingBox(-20, -20, 40, 40), marginRatio: 0f, maxSide: 256);
        tools.ReadSize(clipped).ShouldBe(new ImageSize(20, 20));

        // Широкая рамка: ужатие по большей стороне с сохранением пропорций.
        var wide = tools.CropJpeg(source, new BoundingBox(0, 0, 200, 100), marginRatio: 0f, maxSide: 50);
        tools.ReadSize(wide).ShouldBe(new ImageSize(50, 25));

        // Рамка целиком вне изображения — ошибка входа, не пустой JPEG.
        Should.Throw<ArgumentException>(() => tools.CropJpeg(source, new BoundingBox(500, 500, 10, 10)));
    }
}
