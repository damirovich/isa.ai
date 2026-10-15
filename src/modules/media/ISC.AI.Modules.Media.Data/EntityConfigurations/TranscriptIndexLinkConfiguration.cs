using ISC.AI.Modules.Media.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ISC.AI.Modules.Media.Data.EntityConfigurations;

/// <summary>Конфигурация мостика расшифровка ↔ корпус ядра (<c>media.transcript_index_link</c>, ADR-0051).</summary>
public sealed class TranscriptIndexLinkConfiguration : IEntityTypeConfiguration<TranscriptIndexLink>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<TranscriptIndexLink> builder)
    {
        builder.ToTable("transcript_index_link", MediaDbContext.Schema);
        builder.HasKey(l => l.Id);

        // Одна строка на носитель; каскад внутри схемы — документ корпуса уничтожает MediaPurger до этого.
        builder.HasIndex(l => l.AssetId).IsUnique();
        builder.HasIndex(l => l.CoreDocumentId);
        builder.HasOne(l => l.Asset).WithMany()
               .HasForeignKey(l => l.AssetId).OnDelete(DeleteBehavior.Cascade);
    }
}
