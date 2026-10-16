using ISC.AI.Profile.Investigation.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ISC.AI.Profile.Investigation.Data.EntityConfigurations;

/// <summary>Конфигурация материалов дел в корпусе ядра (<c>investigation.case_corpus_entry</c>, ADR-0051).</summary>
public sealed class CaseCorpusEntryConfiguration : IEntityTypeConfiguration<CaseCorpusEntry>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<CaseCorpusEntry> builder)
    {
        builder.ToTable("case_corpus_entry", InvestigationDbContext.Schema);
        builder.HasKey(e => e.Id);

        builder.Property(e => e.SourceVersion).HasMaxLength(200).IsRequired();

        // Один текст на источник; документы корпуса уничтожает уничтожение дела ДО удаления строк (каскад — после).
        builder.HasIndex(e => new { e.Kind, e.SourceId }).IsUnique();
        builder.HasIndex(e => e.CaseId);
        builder.HasOne(e => e.Case).WithMany()
               .HasForeignKey(e => e.CaseId).OnDelete(DeleteBehavior.Cascade);
    }
}
