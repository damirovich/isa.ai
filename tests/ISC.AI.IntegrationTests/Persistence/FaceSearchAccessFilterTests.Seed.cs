using ISC.AI.Modules.Media.Data;
using ISC.AI.Modules.Media.Data.Entities;
using ISC.AI.Modules.Media.Domain.Model;
using Pgvector;

namespace ISC.AI.IntegrationTests.Persistence;

// Посев данных GATE-4: шесть шаблонов, из которых субъекту (гриф ≤ 1, подразделение 7)
// по умолчанию доступен ровно один.
public sealed partial class FaceSearchAccessFilterTests
{
    // Единичный вектор [1,0,...]: всем шаблонам — он же, чтобы ранжирование не маскировало решётку.
    private static float[] Probe()
    {
        var v = new float[FaceTemplate.Dimensions];
        v[0] = 1f;
        return v;
    }

    private static float[] Orthogonal()
    {
        var v = new float[FaceTemplate.Dimensions];
        v[1] = 1f;
        return v;
    }

    private static async Task SeedAsync(MediaDbContext db)
    {
        await AddAssetWithTemplateAsync(db, classification: 1, divisionId: 7, isCurrent: true, acceptable: true);   // ДОСТУПЕН
        await AddAssetWithTemplateAsync(db, classification: 2, divisionId: 7, isCurrent: true, acceptable: true);   // выше допуска
        await AddAssetWithTemplateAsync(db, classification: 1, divisionId: 8, isCurrent: true, acceptable: true);   // чужое подразделение
        await AddAssetWithTemplateAsync(db, classification: 1, divisionId: 7, isCurrent: false, acceptable: true);  // неактуальный носитель
        await AddAssetWithTemplateAsync(db, classification: 1, divisionId: 7, isCurrent: true, acceptable: false);  // непригодное лицо
        await AddAssetWithTemplateAsync(db, classification: 0, divisionId: 999, isCurrent: true, acceptable: true); // чужое подразделение, низкий гриф
    }

    private static async Task<int> AddAssetWithTemplateAsync(
        MediaDbContext db, short classification, int divisionId, bool isCurrent, bool acceptable, float[]? vector = null)
    {
        var asset = new MediaAsset
        {
            Kind = MediaKind.Image,
            OriginalFileName = "a.jpg",
            StoredFileName = Guid.NewGuid().ToString("N") + ".jpg",
            ContentType = "image/jpeg",
            ContentHash = Guid.NewGuid().ToString("N"),
            ByteSize = 1,
            Classification = classification,
            DivisionId = divisionId,
            IsCurrent = isCurrent,
            IndexStatus = MediaIndexStatus.Indexed,
        };
        db.Assets.Add(asset);
        await db.SaveChangesAsync();

        var face = new Face
        {
            AssetId = asset.Id,
            BoxX = 1, BoxY = 1, BoxWidth = 50, BoxHeight = 50,
            Landmarks = new float[10],
            DetectionScore = 0.95f,
            QualityScore = acceptable ? 0.9f : 0.1f,
            QualityAcceptable = acceptable,
            Classification = classification,
            DivisionId = divisionId,
        };
        db.Faces.Add(face);
        await db.SaveChangesAsync();

        db.Templates.Add(new FaceTemplate
        {
            FaceId = face.Id,
            AssetId = asset.Id,
            Embedding = new Vector(vector ?? Probe()),
            ModelVersion = "sface-test",
            QualityAcceptable = acceptable,
            Classification = classification,
            DivisionId = divisionId,
            IsCurrent = isCurrent,
        });
        await db.SaveChangesAsync();
        return asset.Id;
    }
}
