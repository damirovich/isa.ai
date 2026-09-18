using ISC.AI.Modules.Media.Data.Entities;
using ISC.AI.Modules.Media.Domain.Model;
using Microsoft.EntityFrameworkCore;
using Pgvector;

namespace ISC.AI.Modules.Media.Data;

public sealed partial class MediaStore
{
    /// <inheritdoc />
    public async Task CompleteIndexingAsync(
        int assetId,
        IReadOnlyList<IndexedFace> faces,
        string detectorVersion,
        string embedderVersion,
        long? durationMs = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(faces);

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var asset = await db.Assets.SingleAsync(a => a.Id == assetId, cancellationToken);

        // ОДНА транзакция: старые производные снимаются, новые пишутся, статус меняется — либо всё,
        // либо ничего (ТП-005). Кадры/лица/шаблоны носителя каскадом (FK внутри схемы).
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        await db.Faces.Where(f => f.AssetId == assetId).ExecuteDeleteAsync(cancellationToken);
        await db.Frames.Where(f => f.AssetId == assetId).ExecuteDeleteAsync(cancellationToken);

        // Кадры — по одному на уникальный индекс; лица ссылаются на них.
        var frames = new Dictionary<int, MediaFrame>();
        foreach (var indexed in faces.Where(f => f.FrameIndex is not null))
        {
            var index = indexed.FrameIndex!.Value;
            if (!frames.ContainsKey(index))
            {
                var frame = new MediaFrame { AssetId = assetId, Index = index, TimestampMs = indexed.FrameTimestampMs ?? 0 };
                frames[index] = frame;
                db.Frames.Add(frame);
            }
        }

        foreach (var indexed in faces)
        {
            var face = new Face
            {
                AssetId = assetId,
                Frame = indexed.FrameIndex is { } fi ? frames[fi] : null,
                BoxX = indexed.Face.Box.X,
                BoxY = indexed.Face.Box.Y,
                BoxWidth = indexed.Face.Box.Width,
                BoxHeight = indexed.Face.Box.Height,
                Landmarks = indexed.Face.Landmarks.ToArray().SelectMany(p => new[] { p.X, p.Y }).ToArray(),
                DetectionScore = indexed.Face.Score,
                QualityScore = indexed.Quality.Score,
                QualityAcceptable = indexed.Quality.Acceptable,
                QualityReason = indexed.Quality.Reason,
                CropStoredFileName = indexed.CropStoredFileName,
                TrackId = indexed.TrackId,
                // Денормализация режимных полей с носителя (ТБ-020): строка самодостаточна для фильтра.
                Classification = asset.Classification,
                DivisionId = asset.DivisionId,
            };
            db.Faces.Add(face);

            if (indexed.Template is { } template)
            {
                if (template.Length != FaceTemplate.Dimensions)
                {
                    throw new InvalidOperationException(
                        $"Размерность шаблона {template.Length} не совпадает со схемой {FaceTemplate.Dimensions} (ADR-0020).");
                }

                db.Templates.Add(new FaceTemplate
                {
                    Face = face,
                    AssetId = assetId,
                    Embedding = new Vector(template),
                    ModelVersion = embedderVersion,
                    QualityAcceptable = indexed.Quality.Acceptable,
                    Classification = asset.Classification,
                    DivisionId = asset.DivisionId,
                    IsCurrent = asset.IsCurrent,
                });
            }
        }

        asset.IndexStatus = MediaIndexStatus.Indexed;
        asset.IndexError = null;
        asset.DetectorVersion = detectorVersion;
        asset.EmbedderVersion = embedderVersion;
        asset.DurationMs = durationMs ?? asset.DurationMs;
        asset.IndexedAt = DateTime.UtcNow;

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}
