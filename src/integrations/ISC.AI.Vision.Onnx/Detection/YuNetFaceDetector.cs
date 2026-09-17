using ISC.AI.Modules.Media.Domain.Model;
using ISC.AI.Modules.Media.Domain.Services;
using ISC.AI.Vision.Onnx.Imaging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SkiaSharp;

namespace ISC.AI.Vision.Onnx.Detection;

/// <summary>
/// Детектор лиц YuNet (OpenCV Zoo, MIT) через ONNX Runtime в процессе (ADR-0020). Вход — растр BGR
/// 0..255 без нормализации (как <c>blobFromImage</c> в OpenCV); изображение вписывается в размер входа
/// модели с сохранением пропорций (letterbox, дополнение чёрным справа и снизу); выходы — по три
/// тензора cls/obj/bbox/kps на шаги 8/16/32; координаты возвращаются в пикселях ИСХОДНОГО изображения.
/// </summary>
/// <remarks>
/// Размер входа берётся из метаданных модели: у <c>face_detection_yunet_2023mar.onnx</c> он фиксирован
/// (1×3×640×640) — проверено живым прогоном 17.09.2026; для динамического входа изображение
/// уменьшается до <see cref="VisionOptions.MaxInputSide"/> и дополняется до кратности 32.
/// Сессия ONNX создаётся лениво один раз после проверки пина SHA-256 (ТИ-004); Run потокобезопасен,
/// экземпляр — singleton. Порядок ключевых точек модели: правый глаз человека (слева на изображении),
/// левый глаз, нос, правый и левый уголки рта — совпадает с шаблоном выравнивания SFace.
/// </remarks>
public sealed class YuNetFaceDetector(VisionOptions options) : IFaceDetector, IDisposable
{
    private const int Alignment = 32;
    private readonly Lazy<InferenceSession> _session = new(() => CreateSession(options));

    /// <inheritdoc />
    public string ModelVersion => "yunet-2023mar";

    /// <inheritdoc />
    public Task<IReadOnlyList<DetectedFace>> DetectAsync(byte[] imageBytes, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var session = _session.Value;

        using var original = ImageDecoder.DecodeRgba(imageBytes);
        var (targetW, targetH, scale) = ResolveInputGeometry(session, original.Width, original.Height);
        using var scaled = ImageDecoder.ScaleBy(original, scale);
        var input = ToBgrTensor(scaled, targetW, targetH);

        var inputName = session.InputMetadata.Keys.First();
        using var results = session.Run([NamedOnnxValue.CreateFromTensor(inputName, input)]);
        var outputs = results.ToDictionary(r => r.Name, r => r.AsTensor<float>().ToArray());

        var candidates = YuNetDecoder.Decode(outputs, targetW, targetH, options.DetectionScoreThreshold);
        var kept = YuNetDecoder.NonMaximumSuppression(candidates, options.NmsIouThreshold);

        // Обратно в координаты исходного изображения и обрезка по границам кадра.
        var faces = kept.Select(f => Rescale(f, 1f / scale, original.Width, original.Height)).ToList();
        return Task.FromResult<IReadOnlyList<DetectedFace>>(faces);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_session.IsValueCreated)
        {
            _session.Value.Dispose();
        }
    }

    private static InferenceSession CreateSession(VisionOptions options)
    {
        var path = ModelFileIntegrity.EnsureTrusted(options.DetectorModelPath, options.DetectorSha256, "детектор лиц YuNet");
        using var sessionOptions = OnnxSessions.Quiet();
        return new InferenceSession(path, sessionOptions);
    }

    // Фиксированный вход модели → letterbox под него (масштаб по меньшей из сторон, может быть > 1);
    // динамический → уменьшение до MaxInputSide и дополнение до кратности 32 (без увеличения).
    private (int Width, int Height, float Scale) ResolveInputGeometry(InferenceSession session, int width, int height)
    {
        var dims = session.InputMetadata.Values.First().Dimensions;
        if (dims.Length == 4 && dims[2] > 0 && dims[3] > 0)
        {
            var targetH = dims[2];
            var targetW = dims[3];
            var scale = MathF.Min((float)targetW / width, (float)targetH / height);
            return (targetW, targetH, scale);
        }

        var fit = MathF.Min(1f, (float)options.MaxInputSide / Math.Max(width, height));
        return (
            RoundUp((int)MathF.Round(width * fit), Alignment),
            RoundUp((int)MathF.Round(height * fit), Alignment),
            fit);
    }

    private static int RoundUp(int value, int multiple) => Math.Max(multiple, (value + multiple - 1) / multiple * multiple);

    // NCHW, BGR, float 0..255; растр кладётся в левый верхний угол, остальное — чёрное.
    private static DenseTensor<float> ToBgrTensor(SKBitmap rgba, int padW, int padH)
    {
        var tensor = new DenseTensor<float>([1, 3, padH, padW]);
        var pixels = rgba.GetPixelSpan();
        var width = Math.Min(rgba.Width, padW);
        var height = Math.Min(rgba.Height, padH);
        var rowBytes = rgba.RowBytes;
        for (var y = 0; y < height; y++)
        {
            var row = pixels.Slice(y * rowBytes, width * 4);
            for (var x = 0; x < width; x++)
            {
                var p = x * 4;
                tensor[0, 0, y, x] = row[p + 2]; // B
                tensor[0, 1, y, x] = row[p + 1]; // G
                tensor[0, 2, y, x] = row[p];     // R
            }
        }

        return tensor;
    }

    private static DetectedFace Rescale(DetectedFace face, float factor, int width, int height)
    {
        var x = Math.Clamp(face.Box.X * factor, 0f, width);
        var y = Math.Clamp(face.Box.Y * factor, 0f, height);
        var right = Math.Clamp(face.Box.Right * factor, 0f, width);
        var bottom = Math.Clamp(face.Box.Bottom * factor, 0f, height);
        var box = new BoundingBox(x, y, right - x, bottom - y);

        FacePoint P(FacePoint point) => new(point.X * factor, point.Y * factor);
        var lm = face.Landmarks;
        var landmarks = new FaceLandmarks(P(lm.LeftEye), P(lm.RightEye), P(lm.Nose), P(lm.LeftMouth), P(lm.RightMouth));
        return new DetectedFace(box, landmarks, face.Score);
    }
}
