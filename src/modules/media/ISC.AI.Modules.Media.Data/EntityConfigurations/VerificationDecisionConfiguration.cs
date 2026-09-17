using ISC.AI.Modules.Media.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ISC.AI.Modules.Media.Data.EntityConfigurations;

/// <summary>Конфигурация таблицы решений верификации (<c>media.verification_decision</c>, ТБ-073).</summary>
public sealed class VerificationDecisionConfiguration : IEntityTypeConfiguration<VerificationDecisionEntity>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<VerificationDecisionEntity> builder)
    {
        builder.ToTable("verification_decision", MediaDbContext.Schema);
        builder.HasKey(d => d.Id);

        builder.Property(d => d.Rationale).HasMaxLength(4000).IsRequired();

        // ОДНО решение на стадию (ТБ-073): повторное решение эксперта/верификатора по тому же кандидату
        // невозможно на уровне БД — попытка обойти валидатор упирается в уникальный индекс.
        builder.HasIndex(d => new { d.CandidateId, d.Stage }).IsUnique();

        // Каскад ВНУТРИ схемы: кандидат → решения.
        builder.HasOne<SearchCandidate>().WithMany()
               .HasForeignKey(d => d.CandidateId).OnDelete(DeleteBehavior.Cascade);
    }
}
