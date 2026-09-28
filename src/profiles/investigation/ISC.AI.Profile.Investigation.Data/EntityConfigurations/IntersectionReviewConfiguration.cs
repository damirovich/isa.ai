using ISC.AI.Profile.Investigation.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ISC.AI.Profile.Investigation.Data.EntityConfigurations;

/// <summary>Конфигурация решений по пересечениям (<c>investigation.intersection_review</c>, ТФ-ПЕР-07, ADR-0029).</summary>
public class IntersectionReviewConfiguration : IEntityTypeConfiguration<IntersectionReview>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<IntersectionReview> builder)
    {
        builder.ToTable("intersection_review", InvestigationDbContext.Schema);
        builder.HasKey(e => e.Id);

        builder.Property(e => e.Kind).IsRequired();
        builder.Property(e => e.KeyNormalized).HasMaxLength(1000).IsRequired();
        builder.Property(e => e.Decision).IsRequired();
        builder.Property(e => e.DecidedAt).IsRequired();

        // Режимные поля — с фигуранта своего дела (ТБ-070), NOT NULL.
        builder.Property(e => e.Classification).IsRequired();
        builder.Property(e => e.DivisionId).IsRequired();

        // FK внутри схемы: удаление фигуранта (и дела каскадом, ADR-0025) уносит решение.
        // На чужое дело (OtherCaseId) внешнего ключа НЕТ нарочно: ссылка только по значению (ТФ-ПЕР-07),
        // уничтожение чужого дела не трогает строки своего.
        builder.HasOne(e => e.Person).WithMany()
               .HasForeignKey(e => e.PersonId).OnDelete(DeleteBehavior.Cascade);

        // Одно решение на (фигурант, вид, ключ, чужое дело): повторное — перезапись, история — в аудите.
        builder.HasIndex(e => new { e.PersonId, e.Kind, e.KeyNormalized, e.OtherCaseId }).IsUnique();
    }
}
