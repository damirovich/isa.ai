using ISC.AI.Modules.Media.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ISC.AI.Modules.Media.Data.EntityConfigurations;

/// <summary>Конфигурация таблицы дикторов расшифровки (<c>media.transcript_speaker</c>, ADR-0062).</summary>
public sealed class TranscriptSpeakerConfiguration : IEntityTypeConfiguration<TranscriptSpeaker>
{
    /// <summary>Предел длины имени диктора.</summary>
    public const int NameMaxLength = 100;

    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<TranscriptSpeaker> builder)
    {
        builder.ToTable("transcript_speaker", MediaDbContext.Schema);
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Name).HasMaxLength(NameMaxLength);
        builder.Property(s => s.Classification).IsRequired();
        builder.Property(s => s.DivisionId).IsRequired();

        // Один диктор с номером на носитель.
        builder.HasIndex(s => new { s.AssetId, s.Number }).IsUnique();

        // Каскад ВНУТРИ схемы (ТБ-064): уничтожение носителя снимает и дикторов.
        builder.HasOne(s => s.Asset).WithMany()
               .HasForeignKey(s => s.AssetId).OnDelete(DeleteBehavior.Cascade);
    }
}
