using ISC.AI.Modules.Media.Domain.Model;
using ISC.AI.Modules.Media.Domain.Services;
using ISC.AI.Vision.Onnx.Imaging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SkiaSharp;

namespace ISC.AI.Vision.Onnx.Embedding;

/// <summary>
/// Векторизатор лиц SFace (OpenCV Zoo, Apache-2.0) через ONNX Runtime (ADR-0020): лицо выравнивается
/// преобразованием подобия по пяти точкам в растр 112×112, подаётся как RGB 0..255 (как
/// <c>blobFromImage(..., swapRB=true)</c> в OpenCV), выход 128 чисел L2-нормируется — косинусная
/// схожесть шаблонов равна скалярному произведению (ТО-мат-08).
/// </summary>
/// <remarks>
/// Сессия — лениво один раз после проверки пина SHA-256 (ТИ-004, ~37 МБ); Run потокобезопасен.
/// Стартовые пороги схожести из OpenCV (cosine ≥ 0,363 на LFW) — только точка отсчёта; рабочие —
/// по пилоту (ТО-мат-08). Пустое лицо/вырожденные точки — явная ошибка, не нулевой вектор.
/// </remarks>
public sealed class SFaceEmbedder(VisionOptions options) : IFaceEmbedder, IDisposable
{
    private const int Side = 112;
    private readonly Lazy<InferenceSession> _session = new(() => CreateSession(options));

    /// <inheritdoc />
    public int Dimensions => 128;

    /// <inheritdoc />
    public string ModelVersion => "sface-2021dec";

    /// <inheritdoc />
    public Task<float[]> EmbedAsync(byte[] imageBytes, DetectedFace face, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(face);
        cancellationToken.ThrowIfCancellationRequested();

        using var source = ImageDecoder.DecodeRgba(imageBytes);
        using var aligned = AlignCrop(source, face.Landmarks);
        var input = ToRgbTensor(aligned);

        var session = _session.Value;
        var inputName = session.InputMetadata.Keys.First();
        using var results = session.Run([NamedOnnxValue.CreateFromTensor(inputName, input)]);
        var raw = results[0].AsTensor<float>().ToArray();
        if (raw.Length != Dimensions)
        {
            throw new InvalidOperationException(
                $"Модель SFace вернула вектор длины {raw.Length}, ожидалось {Dimensions}: файл модели не тот (ТИ-004).");
        }

        return Task.FromResult(Normalize(raw));
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_session.IsValueCreated)
        {
            _session.Value.Dispose();
        }
    }

    /// <summary>Выравнивание: пять точек лица → шаблон 112×112 преобразованием подобия (без отражения).</summary>
    internal static SKBitmap AlignCrop(SKBitmap source, FaceLandmarks landmarks)
    {
        var transform = SimilarityTransform.Estimate(landmarks.ToArray(), SimilarityTransform.SFaceTemplate);
        var matrix = new SKMatrix(
            transform.A, -transform.B, transform.Tx,
            transform.B, transform.A, transform.Ty,
            0f, 0f, 1f);

        var aligned = new SKBitmap(new SKImageInfo(Side, Side, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(aligned);
        canvas.Clear(SKColors.Black);
        canvas.SetMatrix(matrix);
        canvas.DrawBitmap(source, 0f, 0f, new SKSamplingOptions(SKFilterMode.Linear));
        return aligned;
    }

    private static InferenceSession CreateSession(VisionOptions options)
    {
        var path = ModelFileIntegrity.EnsureTrusted(options.EmbedderModelPath, options.EmbedderSha256, "векторизатор лиц SFace");
        using var sessionOptions = OnnxSessions.Quiet();
        return new InferenceSession(path, sessionOptions);
    }

    // NCHW, RGB, float 0..255.
    private static DenseTensor<float> ToRgbTensor(SKBitmap rgba)
    {
        var tensor = new DenseTensor<float>([1, 3, Side, Side]);
        var pixels = rgba.GetPixelSpan();
        var rowBytes = rgba.RowBytes;
        for (var y = 0; y < Side; y++)
        {
            var row = pixels.Slice(y * rowBytes, Side * 4);
            for (var x = 0; x < Side; x++)
            {
                var p = x * 4;
                tensor[0, 0, y, x] = row[p];     // R
                tensor[0, 1, y, x] = row[p + 1]; // G
                tensor[0, 2, y, x] = row[p + 2]; // B
            }
        }

        return tensor;
    }

    /// <summary>L2-нормализация; нулевой вектор — ошибка (сломанный вход, не «лицо без признаков»).</summary>
    internal static float[] Normalize(float[] vector)
    {
        double sum = 0;
        foreach (var v in vector)
        {
            sum += (double)v * v;
        }

        var norm = Math.Sqrt(sum);
        if (norm <= 1e-12)
        {
            throw new InvalidOperationException("Векторизатор вернул нулевой вектор — вход непригоден.");
        }

        var result = new float[vector.Length];
        for (var i = 0; i < vector.Length; i++)
        {
            result[i] = (float)(vector[i] / norm);
        }

        return result;
    }
}
