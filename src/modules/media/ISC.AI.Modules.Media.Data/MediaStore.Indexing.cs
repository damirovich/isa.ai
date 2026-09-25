using System.Globalization;
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
        VideoProbe? probe = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(faces);

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        // ОДНА транзакция: старые производные снимаются, новые пишутся, статус меняется — либо всё,
        // либо ничего (ТП-005). Кадры/лица/шаблоны носителя каскадом (FK внутри схемы).
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // Блокировка строки носителя до конца транзакции — та же, что берут запись расшифровки и уничтожение
        // носителя (MediaPurger, ТБ-064): уничтожение не может посчитать лица для акта, пока результат
        // индексации фиксируется, и не расходится с тем, что снесёт каскад. Носитель уничтожен, пока шла
        // индексация, — строки нет: исключение, индексатор снимет свои вырезки и зафиксирует сбой.
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT 1 FROM media.asset WHERE id = {assetId} FOR UPDATE", cancellationToken);

        var asset = await db.Assets.SingleOrDefaultAsync(a => a.Id == assetId, cancellationToken)
            ?? throw new InvalidOperationException(
                "Носитель " + assetId.ToString(CultureInfo.InvariantCulture) + " не найден: результат индексации записать некуда.");

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
        if (probe is not null)
        {
            // Проба видеопотока (ADR-0028) — ПОВЕРХ прежних значений: переиндексация обновляет частоту, размер
            // кадра и точную длительность (таймкод последнего кадра выборки — лишь запасная оценка без пробы).
            // Длительность из пробы может быть неизвестна (незавершённый Matroska/WebM) — тогда она и остаётся
            // неизвестной (null), а НЕ подменяется оценкой по раскадровке: при известной частоте кадров длительность
            // считается точной и по ней отсекаются моменты «за концом записи» (эндпоинт кадра, снимок); оценка,
            // округлённая вниз до шага выборки, отрезала бы хвост записи. Точную длительность такого файла позже
            // даёт расшифровка речи (длина звука), если она есть.
            asset.FrameRate = probe.FrameRate;
            asset.FrameWidth = probe.Width;
            asset.FrameHeight = probe.Height;
            asset.DurationMs = probe.DurationMs;
        }
        else if (asset.FrameRate is null)
        {
            // Пробы нет и раньше не было: оценка по раскадровке. Если проба была (частота известна), точную
            // длительность оценкой не перетирать — сбой пробы при переиндексации не должен огрублять данные.
            asset.DurationMs = durationMs ?? asset.DurationMs;
        }

        asset.IndexedAt = DateTime.UtcNow;

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}
