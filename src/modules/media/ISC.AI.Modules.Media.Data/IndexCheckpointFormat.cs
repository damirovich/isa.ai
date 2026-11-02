using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using ISC.AI.Modules.Media.Domain.Model;

namespace ISC.AI.Modules.Media.Data;

/// <summary>
/// Формат лиц в части незавершённой индексации (ADR-0064): JSON с короткими именами полей; шаблон — base64 байтов float32
/// little-endian, чтобы повтор получил ровно те же числа, что дал векторизатор. Версия — в самом JSON: часть неизвестной
/// версии не читается (<see cref="FormatException"/>), и индексатор снимает её и начинает прогон заново.
/// </summary>
internal static class IndexCheckpointFormat
{
    /// <summary>Версия формата; меняется при любом изменении состава полей.</summary>
    public const int Version = 1;

    // NaN и бесконечности в оценках кадра (вырожденное лицо) пишутся строками — иначе сериализатор бросил бы на них.
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.General)
    {
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    /// <summary>Лица части → JSON. У каждого лица должен быть номер кадра: часть бывает только у видео.</summary>
    public static string Write(IReadOnlyList<IndexedFace> faces)
    {
        ArgumentNullException.ThrowIfNull(faces);
        return JsonSerializer.Serialize(new Envelope(Version, faces.Select(ToRow).ToArray()), Options);
    }

    /// <summary>JSON части → лица в порядке записи; повреждённая часть или чужая версия — <see cref="FormatException"/>.</summary>
    public static IReadOnlyList<IndexedFace> Read(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        Envelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<Envelope>(json, Options);
        }
        catch (JsonException exception)
        {
            throw new FormatException("Часть индексации не читается: " + exception.Message, exception);
        }

        if (envelope is null || envelope.Version != Version)
        {
            throw new FormatException(
                "Часть индексации другой версии формата (" + (envelope?.Version.ToString(CultureInfo.InvariantCulture) ?? "нет")
                + "), ожидалась " + Version.ToString(CultureInfo.InvariantCulture) + ".");
        }

        return (envelope.Faces ?? []).Select(FromRow).ToList();
    }

    private static Row ToRow(IndexedFace face)
    {
        var frame = face.FrameIndex
            ?? throw new ArgumentException("У лица части индексации нет номера кадра: части бывают только у видео.", nameof(face));
        var box = face.Face.Box;
        var metrics = face.Metrics;
        return new Row(
            frame,
            face.FrameTimestampMs ?? 0,
            [box.X, box.Y, box.Width, box.Height],
            face.Face.Landmarks.ToArray().SelectMany(p => new[] { p.X, p.Y }).ToArray(),
            face.Face.Score,
            face.Quality.Score,
            face.Quality.Acceptable,
            face.Quality.Reason,
            face.Template is { } template ? EncodeTemplate(template) : null,
            metrics is null
                ? null
                : [metrics.Sharpness, metrics.Lighting, metrics.YawDegrees, metrics.PitchDegrees, metrics.RollDegrees, metrics.EyeDistance, metrics.Score]);
    }

    private static IndexedFace FromRow(Row row)
    {
        if (row.Box is not { Length: 4 } b || row.Landmarks is not { Length: 10 } l || row.Metrics is { Length: not 7 })
        {
            throw new FormatException("Лицо в части индексации повреждено: неверное число координат или оценок.");
        }

        var landmarks = new FaceLandmarks(
            new FacePoint(l[0], l[1]), new FacePoint(l[2], l[3]), new FacePoint(l[4], l[5]),
            new FacePoint(l[6], l[7]), new FacePoint(l[8], l[9]));
        var metrics = row.Metrics is { } m ? new FrameMetrics(m[0], m[1], m[2], m[3], m[4], m[5], m[6]) : null;

        return new IndexedFace(
            row.Frame,
            row.TimestampMs,
            new DetectedFace(new BoundingBox(b[0], b[1], b[2], b[3]), landmarks, row.Score),
            new FaceQuality(row.QualityScore, row.Acceptable, row.Reason),
            row.Template is { } template ? DecodeTemplate(template) : null,
            Metrics: metrics);
    }

    private static string EncodeTemplate(float[] template)
    {
        var bytes = new byte[template.Length * sizeof(float)];
        for (var i = 0; i < template.Length; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * sizeof(float)), template[i]);
        }

        return Convert.ToBase64String(bytes);
    }

    private static float[] DecodeTemplate(string base64)
    {
        var bytes = Convert.FromBase64String(base64);
        if (bytes.Length % sizeof(float) != 0)
        {
            throw new FormatException("Шаблон в части индексации повреждён: длина не кратна 4 байтам.");
        }

        var template = new float[bytes.Length / sizeof(float)];
        for (var i = 0; i < template.Length; i++)
        {
            template[i] = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(i * sizeof(float)));
        }

        return template;
    }

    /// <summary>Часть целиком: версия формата и лица.</summary>
    internal sealed record Envelope(
        [property: JsonPropertyName("v")] int Version,
        [property: JsonPropertyName("faces")] Row[]? Faces);

    /// <summary>Лицо части: короткие имена — частей в долгом видео сотни, лиц — тысячи.</summary>
    internal sealed record Row(
        [property: JsonPropertyName("f")] int Frame,
        [property: JsonPropertyName("t")] long TimestampMs,
        [property: JsonPropertyName("b")] float[]? Box,
        [property: JsonPropertyName("l")] float[]? Landmarks,
        [property: JsonPropertyName("s")] float Score,
        [property: JsonPropertyName("qs")] float QualityScore,
        [property: JsonPropertyName("qa")] bool Acceptable,
        [property: JsonPropertyName("qr")] string? Reason,
        [property: JsonPropertyName("e")] string? Template,
        [property: JsonPropertyName("m")] float[]? Metrics);
}
