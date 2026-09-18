using ISC.AI.Modules.Media.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ISC.AI.Modules.Media.Data.EntityConfigurations;

/// <summary>Конфигурация таблицы шаблонов (<c>media.face_template</c>) с вектором pgvector (ТС-012).</summary>
public sealed class FaceTemplateConfiguration : IEntityTypeConfiguration<FaceTemplate>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<FaceTemplate> builder)
    {
        builder.ToTable("face_template", MediaDbContext.Schema);
        builder.HasKey(t => t.Id);

        // Тип столбца vector(N) фиксирует размерность модели (ADR-0020).
        builder.Property(t => t.Embedding)
               .HasColumnType($"vector({FaceTemplate.Dimensions})")
               .IsRequired();

        builder.Property(t => t.ModelVersion).HasMaxLength(50).IsRequired();

        // Режим + актуальность денормализованы на вектор (ТБ-020/070, ADR-0013). NOT NULL.
        builder.Property(t => t.Classification).IsRequired();
        builder.Property(t => t.DivisionId).IsRequired();
        builder.Property(t => t.IsCurrent).IsRequired().HasDefaultValue(true);

        // Pre-filter доступа на стороне БД (ТБ-020): B-tree по режимным полям рядом с ANN-поиском.
        builder.HasIndex(t => new { t.Classification, t.DivisionId });
        builder.HasIndex(t => t.AssetId);
        builder.HasIndex(t => t.FaceId).IsUnique();

        // ANN-индекс: HNSW + косинусная метрика (ADR-0020, ТБ-022). Пара ef_construction=512 при
        // сборке + hnsw.ef_search=200 при поиске — единственное проверенное лечение потери малых
        // «островов» графа (инцидент 26.08.2026 на core.embedding; здесь та же ситуация: у одного
        // человека — считанные шаблоны среди тысяч чужих). m=16 — дефолт, зафиксирован явно.
        builder.HasIndex(t => t.Embedding)
               .HasMethod("hnsw")
               .HasOperators("vector_cosine_ops")
               .HasStorageParameter("m", 16)
               .HasStorageParameter("ef_construction", 512);

        // Каскад ВНУТРИ схемы (ТБ-064, GATE-6): лицо → шаблон; носитель → шаблон (прямой FK, чтобы
        // массовое удаление по носителю не зависело от порядка каскадов через лицо).
        builder.HasOne(t => t.Face).WithOne()
               .HasForeignKey<FaceTemplate>(t => t.FaceId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<MediaAsset>().WithMany()
               .HasForeignKey(t => t.AssetId).OnDelete(DeleteBehavior.Cascade);
    }
}
